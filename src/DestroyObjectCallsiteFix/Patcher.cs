using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

public static class Patcher
{
    private const string TargetTypeName = "PhysGrabObjectImpactDetector";
    private const string TargetMethodName = "DestroyObject";

    // BepInEx 5 preloader contract. We intentionally do not reference Assembly-CSharp
    // from this patcher; the game assembly is supplied to Patch() as Cecil metadata.
    public static IEnumerable<string> TargetDLLs
    {
        get { return new[] { "Assembly-CSharp.dll" }; }
    }

    public static void Patch(AssemblyDefinition gameAssembly)
    {
        try
        {
            if (gameAssembly == null || gameAssembly.MainModule == null ||
                !string.Equals(gameAssembly.Name.Name, "Assembly-CSharp", StringComparison.OrdinalIgnoreCase))
                return;

            TypeDefinition targetType = FindType(gameAssembly.MainModule.Types, TargetTypeName);
            MethodDefinition targetMethod = targetType == null
                ? null
                : targetType.Methods.FirstOrDefault(IsCurrentTwoBoolOverload);

            if (targetMethod == null)
            {
                Log("Skipped: current Assembly-CSharp does not expose void " +
                    TargetTypeName + "." + TargetMethodName + "(bool, bool).");
                return;
            }

            string gameRoot = FindGameRoot();
            if (gameRoot == null)
            {
                Log("Skipped: could not locate BepInEx/plugins or BepInEx/patchers.");
                return;
            }

            string[] scanDirectories =
            {
                Path.Combine(gameRoot, "BepInEx", "plugins"),
                Path.Combine(gameRoot, "BepInEx", "patchers")
            };

            int filesScanned = 0;
            int filesChanged = 0;
            int callSitesChanged = 0;

            foreach (string directory in scanDirectories)
            {
                if (!Directory.Exists(directory))
                    continue;

                string[] files;
                try
                {
                    files = Directory.GetFiles(directory, "*.dll", SearchOption.AllDirectories);
                }
                catch (Exception ex)
                {
                    Log("Could not enumerate " + directory + ": " + ex.Message);
                    continue;
                }

                foreach (string file in files)
                {
                    if (IsThisPatcher(file) || string.Equals(
                        Path.GetFileName(file), "Assembly-CSharp.dll", StringComparison.OrdinalIgnoreCase))
                        continue;

                    filesScanned++;
                    try
                    {
                        int changedCalls = PatchPluginAssembly(file);
                        if (changedCalls > 0)
                        {
                            filesChanged++;
                            callSitesChanged += changedCalls;
                            Log("Patched " + changedCalls + " legacy call(s) in " + Path.GetFileName(file));
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("Skipped " + Path.GetFileName(file) + ": " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }

            Log("Scan complete. Files scanned: " + filesScanned +
                "; DLLs fixed: " + filesChanged +
                "; DestroyObject call sites fixed: " + callSitesChanged + ".");

            if (callSitesChanged == 0)
            {
                Log("No matching old DestroyObject(bool) references were found in BepInEx/plugins or BepInEx/patchers.");
            }
        }
        catch (Exception ex)
        {
            Log("Patcher failed safely: " + ex);
        }
    }

    private static bool IsCurrentTwoBoolOverload(MethodDefinition method)
    {
        return method != null &&
               method.Name == TargetMethodName &&
               method.ReturnType.MetadataType == MetadataType.Void &&
               method.Parameters.Count == 2 &&
               method.Parameters[0].ParameterType.MetadataType == MetadataType.Boolean &&
               method.Parameters[1].ParameterType.MetadataType == MetadataType.Boolean;
    }

    private static int PatchPluginAssembly(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string tempPath = fullPath + ".destroyobjectfix.tmp";
        string backupPath = fullPath + ".pre-destroyobjectfix.bak";

        int patchedCalls = 0;
        bool changed = false;

        using (AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(
            fullPath,
            new ReaderParameters
            {
                InMemory = true,
                ReadSymbols = false,
                ReadingMode = ReadingMode.Immediate
            }))
        {
            if (assembly.Name != null && assembly.Name.HasPublicKey)
                return 0; // Never invalidate a strong-name signature.

            foreach (ModuleDefinition module in assembly.Modules)
            {
                // A MemberRef can be shared by call sites in different methods in the same module.
                // Keep this set at module scope so every use gets its matching extra stack argument.
                var upgradedReferences = new List<MethodReference>();

                foreach (TypeDefinition type in AllTypes(module.Types))
                {
                    foreach (MethodDefinition method in type.Methods)
                    {
                        if (!method.HasBody || method.Body.Instructions.Count == 0)
                            continue;

                        IList<Instruction> instructions = method.Body.Instructions;

                        for (int i = 0; i < instructions.Count; i++)
                        {
                            Instruction instruction = instructions[i];
                            if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt)
                                continue;

                            MethodReference reference = instruction.Operand as MethodReference;
                            if (reference == null)
                                continue;

                            bool alreadyUpgraded = upgradedReferences.Any(existing => ReferenceEquals(existing, reference));
                            if (!alreadyUpgraded)
                            {
                                if (!IsLegacyDestroyObjectReference(reference))
                                    continue;

                                reference.Parameters.Add(new ParameterDefinition(module.TypeSystem.Boolean));
                                upgradedReferences.Add(reference);
                                changed = true;
                            }

                            // Old call stack: instance, effects.
                            // New call stack: instance, effects, false.
                            // false matches the default used by the existing compatibility shim.
                            Instruction loadDefault = Instruction.Create(OpCodes.Ldc_I4_0);
                            Instruction callInstruction = instruction;
                            instructions.Insert(i, loadDefault);

                            // A branch or switch may target the call directly. Redirect those targets
                            // so none can jump over the newly inserted default argument.
                            foreach (Instruction candidate in instructions)
                            {
                                if (ReferenceEquals(candidate.Operand, callInstruction))
                                    candidate.Operand = loadDefault;
                                else
                                {
                                    Instruction[] targets = candidate.Operand as Instruction[];
                                    if (targets != null)
                                    {
                                        for (int targetIndex = 0; targetIndex < targets.Length; targetIndex++)
                                        {
                                            if (ReferenceEquals(targets[targetIndex], callInstruction))
                                                targets[targetIndex] = loadDefault;
                                        }
                                    }
                                }
                            }

                            foreach (ExceptionHandler handler in method.Body.ExceptionHandlers)
                            {
                                if (ReferenceEquals(handler.TryStart, callInstruction)) handler.TryStart = loadDefault;
                                if (ReferenceEquals(handler.HandlerStart, callInstruction)) handler.HandlerStart = loadDefault;
                                if (ReferenceEquals(handler.FilterStart, callInstruction)) handler.FilterStart = loadDefault;
                            }

                            method.Body.MaxStackSize = Math.Max(method.Body.MaxStackSize, 1) + 1;
                            i++;
                            patchedCalls++;
                        }
                    }
                }
            }

            if (changed)
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);

                assembly.Write(tempPath, new WriterParameters { WriteSymbols = false });
            }
        }

        if (changed)
        {
            try
            {
                if (!File.Exists(backupPath))
                    File.Copy(fullPath, backupPath, false);

                File.Copy(tempPath, fullPath, true);
                File.Delete(tempPath);
            }
            catch
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
                throw;
            }
        }

        return patchedCalls;
    }

    private static bool IsLegacyDestroyObjectReference(MethodReference method)
    {
        if (method.Name != TargetMethodName ||
            method.ReturnType.MetadataType != MetadataType.Void ||
            method.Parameters.Count != 1 ||
            method.Parameters[0].ParameterType.MetadataType != MetadataType.Boolean ||
            !method.HasThis ||
            method.DeclaringType == null ||
            method.DeclaringType.FullName != TargetTypeName)
            return false;

        string scopeName = method.DeclaringType.Scope == null ? null : method.DeclaringType.Scope.Name;
        if (!string.IsNullOrEmpty(scopeName))
        {
            string normalized = Path.GetFileNameWithoutExtension(scopeName);
            if (!string.Equals(normalized, "Assembly-CSharp", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
    {
        foreach (TypeDefinition type in types)
        {
            yield return type;
            foreach (TypeDefinition nested in AllTypes(type.NestedTypes))
                yield return nested;
        }
    }

    private static TypeDefinition FindType(IEnumerable<TypeDefinition> types, string fullName)
    {
        foreach (TypeDefinition type in types)
        {
            if (string.Equals(type.FullName, fullName, StringComparison.Ordinal))
                return type;

            TypeDefinition nested = FindType(type.NestedTypes, fullName);
            if (nested != null)
                return nested;
        }
        return null;
    }

    private static string FindGameRoot()
    {
        var candidates = new List<string>();

        try { candidates.Add(Environment.CurrentDirectory); } catch { }
        try { candidates.Add(AppDomain.CurrentDomain.BaseDirectory); } catch { }

        try
        {
            using (Process process = Process.GetCurrentProcess())
            {
                string executable = process.MainModule == null ? null : process.MainModule.FileName;
                if (!string.IsNullOrEmpty(executable))
                    candidates.Add(Path.GetDirectoryName(executable));
            }
        }
        catch { }

        foreach (string candidate in candidates.Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string plugins = Path.Combine(candidate, "BepInEx", "plugins");
            string patchers = Path.Combine(candidate, "BepInEx", "patchers");
            if (Directory.Exists(plugins) || Directory.Exists(patchers))
                return candidate;
        }

        return null;
    }

    private static bool IsThisPatcher(string path)
    {
        return string.Equals(
            Path.GetFileName(path),
            "REPOCompatibilityPatcher.DestroyObjectCallsiteFix.dll",
            StringComparison.OrdinalIgnoreCase);
    }

    private static void Log(string message)
    {
        string line = "[REPO DestroyObject Callsite Fix] " + message;
        try { Console.WriteLine(line); } catch { }
        try { Trace.WriteLine(line); } catch { }
    }
}