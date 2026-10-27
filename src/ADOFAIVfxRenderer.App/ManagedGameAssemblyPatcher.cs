using System.IO;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ADOFAIVfxRenderer.App;

internal static class ManagedGameAssemblyPatcher
{
    public static void ApplyTextureLimit(string managedDirectory, int maximumSide)
    {
        if (maximumSide <= 0) return;
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
        var loadTexture = textureManager.Methods.FirstOrDefault(static method =>
            method.Name == "LoadTexture" && method.IsStatic && method.Parameters.Count == 3 &&
            method.Parameters[0].ParameterType.FullName == "System.String" &&
            method.Parameters[2].ParameterType.MetadataType == MetadataType.Int32)
                          ?? throw new InvalidOperationException("游戏程序集缺少兼容的 TextureManager.LoadTexture。");
        if (!loadTexture.HasBody || loadTexture.Body.Instructions.Count == 0)
            throw new InvalidOperationException("TextureManager.LoadTexture 没有可修改的方法体。");

        var processor = loadTexture.Body.GetILProcessor();
        var originalStart = loadTexture.Body.Instructions[0];
        processor.InsertBefore(originalStart, processor.Create(OpCodes.Ldarg_2));
        processor.InsertBefore(originalStart, processor.Create(OpCodes.Ldc_I4_M1));
        processor.InsertBefore(originalStart, processor.Create(OpCodes.Bne_Un_S, originalStart));
        processor.InsertBefore(originalStart, processor.Create(OpCodes.Ldc_I4, maximumSide));
        processor.InsertBefore(originalStart, processor.Create(OpCodes.Starg_S, loadTexture.Parameters[2]));

        var replacement = assemblyPath + ".texture-limit";
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
}
