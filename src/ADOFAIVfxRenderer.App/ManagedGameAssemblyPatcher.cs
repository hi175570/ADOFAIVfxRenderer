using System.IO;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ADOFAIVfxRenderer.App;

internal static class ManagedGameAssemblyPatcher
{
    public static void ApplyDynamicTextureStreaming(string managedDirectory)
    {
        var assemblyPath = Path.Combine(managedDirectory, "Assembly-CSharp.dll");
        if (!File.Exists(assemblyPath))
            throw new FileNotFoundException("临时 Worker 缺少游戏程序集。", assemblyPath);

        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(managedDirectory);
        var parameters = new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true,
            ReadSymbols = false,
        };
        using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath, parameters);
        var textureManager = assembly.MainModule.Types.FirstOrDefault(static type => type.Name == "TextureManager")
                             ?? throw new InvalidOperationException("游戏程序集缺少 TextureManager。");
        PatchDecorationSpriteLoading(assembly, textureManager);

        var replacement = assemblyPath + ".texture-streaming";
        try
        {
            assembly.Write(replacement);
            File.Move(replacement, assemblyPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(replacement)) File.Delete(replacement);
        }
    }

    private static void PatchDecorationSpriteLoading(AssemblyDefinition assembly, TypeDefinition textureManager)
    {
        var getOrAddSprite = textureManager.Methods.FirstOrDefault(static method =>
            method.Name == "GetOrAddSprite" && method.Parameters.Count == 3 &&
            method.Parameters[0].ParameterType.FullName == "System.String" &&
            method.Parameters[1].ParameterType.FullName == "System.String" &&
            method.Parameters[2].IsOut)
            ?? throw new InvalidOperationException("游戏程序集缺少兼容的 TextureManager.GetOrAddSprite。");
        var customSprite = textureManager.NestedTypes.FirstOrDefault(static type => type.Name == "CustomSprite")
            ?? throw new InvalidOperationException("游戏程序集缺少 TextureManager.CustomSprite。");

        var workerReference = assembly.MainModule.AssemblyReferences.FirstOrDefault(static reference =>
            reference.Name == "ADOFAIVfxWorker.Runtime");
        if (workerReference == null)
        {
            workerReference = new AssemblyNameReference("ADOFAIVfxWorker.Runtime", new Version(0, 0, 0, 0));
            assembly.MainModule.AssemblyReferences.Add(workerReference);
        }
        var bridgeType = new TypeReference(
            "ADOFAIVfxWorker",
            "DynamicTextureStreaming",
            assembly.MainModule,
            workerReference);
        var bridgeMethod = new MethodReference(
            "GetOrAddPlaceholder",
            assembly.MainModule.TypeSystem.Object,
            bridgeType)
        {
            HasThis = false,
        };
        bridgeMethod.Parameters.Add(new ParameterDefinition(assembly.MainModule.TypeSystem.Object));
        bridgeMethod.Parameters.Add(new ParameterDefinition(assembly.MainModule.TypeSystem.String));
        bridgeMethod.Parameters.Add(new ParameterDefinition(assembly.MainModule.TypeSystem.String));

        var body = getOrAddSprite.Body;
        body.ExceptionHandlers.Clear();
        body.Variables.Clear();
        body.Instructions.Clear();
        body.InitLocals = false;
        var il = body.GetILProcessor();
        // LoadResult.Successful == 0. The bridge preserves a dictionary entry for every
        // requested image key, but only allocates a one-pixel placeholder here. The real
        // texture is decoded later by the Worker when that decoration is visible.
        il.Append(il.Create(OpCodes.Ldarg_3));
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Stind_I4));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldarg_1));
        il.Append(il.Create(OpCodes.Ldarg_2));
        il.Append(il.Create(OpCodes.Call, bridgeMethod));
        il.Append(il.Create(OpCodes.Castclass, customSprite));
        il.Append(il.Create(OpCodes.Ret));
    }
}
