using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

// Answers one question with the real binaries: which submission path does each caller use?
//   Graphic.DrawWorker -> DrawMeshInt -> UnityEngine.Graphics::DrawMesh   (deferred queue)
//   GenDraw.DrawMeshNowOrLater       -> Graphics::DrawMesh / DrawMeshNow  (branch on drawNow)
static class IlScan
{
    static MetadataReader R;
    static PEReader PE;

    static void Open(string path)
    {
        PE = new PEReader(File.OpenRead(path));
        R = PE.GetMetadataReader();
    }

    static string MemberName(EntityHandle h)
    {
        switch (h.Kind)
        {
            case HandleKind.MemberReference:
                var mr = R.GetMemberReference((MemberReferenceHandle)h);
                var parent = mr.Parent;
                string owner = parent.Kind == HandleKind.TypeReference
                    ? R.GetString(R.GetTypeReference((TypeReferenceHandle)parent).Name)
                    : parent.Kind == HandleKind.TypeDefinition
                        ? R.GetString(R.GetTypeDefinition((TypeDefinitionHandle)parent).Name)
                        : "?";
                return owner + "." + R.GetString(mr.Name);
            case HandleKind.MethodDefinition:
                var md = R.GetMethodDefinition((MethodDefinitionHandle)h);
                var td = R.GetTypeDefinition(md.GetDeclaringType());
                return R.GetString(td.Name) + "." + R.GetString(md.Name);
            default:
                return "?";
        }
    }

    static void Scan(string typeName, string methodName)
    {
        foreach (var th in R.TypeDefinitions)
        {
            var td = R.GetTypeDefinition(th);
            if (R.GetString(td.Name) != typeName) continue;
            foreach (var mh in td.GetMethods())
            {
                var md = R.GetMethodDefinition(mh);
                if (R.GetString(md.Name) != methodName) continue;
                if (md.RelativeVirtualAddress == 0) continue;
                var body = PE.GetMethodBody(md.RelativeVirtualAddress);
                var il = body.GetILBytes();
                var calls = new System.Collections.Generic.List<string>();
                for (int i = 0; i + 4 < il.Length; i++)
                {
                    byte op = il[i];
                    if (op == 0x28 || op == 0x6F || op == 0x73)   // call / callvirt / newobj
                    {
                        int token = BitConverter.ToInt32(il, i + 1);
                        try { calls.Add(MemberName(MetadataTokens.EntityHandle(token))); } catch { }
                        i += 4;
                    }
                }
                Console.WriteLine($"{typeName}.{methodName}  il={il.Length}B  calls: " +
                                  string.Join(", ", calls.Distinct().Take(12)));
                return;
            }
        }
        Console.WriteLine($"MISSING {typeName}.{methodName}");
    }

    public static void Run(string acsPath, string stubPath)
    {
        Open(acsPath);
        Scan("Graphic", "DrawWorker");
        Scan("Graphic", "DrawMeshInt");
        Scan("GenDraw", "DrawMeshNowOrLater");
        PE.Dispose();

        Open(stubPath);
        Scan("Graphics", "DrawMesh");
        Scan("Graphics", "DrawMeshNow");
        PE.Dispose();
    }
}
