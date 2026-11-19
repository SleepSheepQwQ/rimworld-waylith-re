using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

namespace IconFlow
{
    // Metadata-only view of one shipped assembly. Nothing here executes game code.
    sealed class Dll
    {
        public string File;
        public PEReader Pe;
        public MetadataReader Md;
        public Sig Sig;

        public static Dll Open(string path)
        {
            // default options keep the image seekable, which GetMethodBody needs
            var pe = new PEReader(System.IO.File.OpenRead(path));
            var d = new Dll { File = path, Pe = pe, Md = pe.GetMetadataReader() };
            d.Sig = new Sig();
            return d;
        }

        public string TypeName(TypeDefinitionHandle h)
        {
            var td = Md.GetTypeDefinition(h);
            string ns = Md.GetString(td.Namespace);
            string n = Md.GetString(td.Name);
            string full = string.IsNullOrEmpty(ns) ? n : ns + "." + n;
            var decl = td.GetDeclaringType();
            if (!decl.IsNil) full = TypeName(decl) + "/" + n;
            return full;
        }

        public string RefName(TypeReferenceHandle h)
        {
            var tr = Md.GetTypeReference(h);
            string ns = Md.GetString(tr.Namespace);
            string n = Md.GetString(tr.Name);
            return string.IsNullOrEmpty(ns) ? n : ns + "." + n;
        }

        public string Describe(EntityHandle h)
        {
            switch (h.Kind)
            {
                case HandleKind.MemberReference:
                {
                    var mr = Md.GetMemberReference((MemberReferenceHandle)h);
                    return ParentName(mr.Parent) + "::" + Md.GetString(mr.Name);
                }
                case HandleKind.MethodDefinition:
                {
                    var md = Md.GetMethodDefinition((MethodDefinitionHandle)h);
                    return TypeName(md.GetDeclaringType()) + "::" + Md.GetString(md.Name);
                }
                case HandleKind.FieldDefinition:
                {
                    var fd = Md.GetFieldDefinition((FieldDefinitionHandle)h);
                    return TypeName(fd.GetDeclaringType()) + "::" + Md.GetString(fd.Name);
                }
                case HandleKind.TypeReference: return RefName((TypeReferenceHandle)h);
                case HandleKind.TypeDefinition: return TypeName((TypeDefinitionHandle)h);
                case HandleKind.TypeSpecification: return SpecName((TypeSpecificationHandle)h);
                case HandleKind.MethodSpecification:
                {
                    var ms = Md.GetMethodSpecification((MethodSpecificationHandle)h);
                    return Describe(ms.Method) + "<>";
                }
                default: return h.Kind.ToString();
            }
        }

        string ParentName(EntityHandle h)
        {
            switch (h.Kind)
            {
                case HandleKind.TypeReference: return RefName((TypeReferenceHandle)h);
                case HandleKind.TypeDefinition: return TypeName((TypeDefinitionHandle)h);
                case HandleKind.MethodDefinition: return TypeName(Md.GetMethodDefinition((MethodDefinitionHandle)h).GetDeclaringType());
                case HandleKind.TypeSpecification: return SpecName((TypeSpecificationHandle)h);
                default: return "?";
            }
        }

        // "Verse.ContentFinder`1<UnityEngine.Texture2D>" for a generic instantiation.
        public string SpecName(TypeSpecificationHandle h)
        {
            try { return Md.GetTypeSpecification(h).DecodeSignature(Sig, null); }
            catch { return "typespec"; }
        }

        public string UserString(int token) => Md.GetUserString(MetadataTokens.UserStringHandle(token & 0xFFFFFF));

        public IEnumerable<MethodDefinitionHandle> Methods(string typeFullName, string methodName)
        {
            foreach (var th in Md.TypeDefinitions)
            {
                if (typeFullName != null && TypeName(th) != typeFullName) continue;
                var td = Md.GetTypeDefinition(th);
                foreach (var mh in td.GetMethods())
                {
                    if (methodName != null && Md.GetString(Md.GetMethodDefinition(mh).Name) != methodName) continue;
                    yield return mh;
                }
            }
        }

        public List<Insn> Body(MethodDefinitionHandle h)
        {
            var md = Md.GetMethodDefinition(h);
            if (md.RelativeVirtualAddress == 0) return new List<Insn>();
            var block = Pe.GetMethodBody(md.RelativeVirtualAddress);
            return MiniIl.Decode(block.GetILBytes());
        }

        public string MethodSignature(MethodDefinitionHandle h)
        {
            var md = Md.GetMethodDefinition(h);
            try
            {
                var sig = md.DecodeSignature(Sig, null);
                return TypeName(md.GetDeclaringType()) + "::" + Md.GetString(md.Name)
                     + "(" + string.Join(", ", sig.ParameterTypes) + ")";
            }
            catch
            {
                return TypeName(md.GetDeclaringType()) + "::" + Md.GetString(md.Name) + "(?)";
            }
        }

        public FieldDefinitionHandle Field(string typeFullName, string fieldName)
        {
            foreach (var th in Md.TypeDefinitions)
            {
                if (TypeName(th) != typeFullName) continue;
                foreach (var fh in Md.GetTypeDefinition(th).GetFields())
                    if (Md.GetString(Md.GetFieldDefinition(fh).Name) == fieldName) return fh;
            }
            return default;
        }

        // Value of a compile-time string constant (ldstr / const field), null when absent.
        public string StringConstant(string typeFullName, string fieldName)
        {
            var fh = Field(typeFullName, fieldName);
            if (fh.IsNil) return null;
            var fd = Md.GetFieldDefinition(fh);
            var ch = fd.GetDefaultValue();
            if (ch.IsNil) return null;
            var c = Md.GetConstant(ch);
            var bytes = Md.GetBlobBytes(c.Value);
            if (c.TypeCode == ConstantTypeCode.String) return Encoding.Unicode.GetString(bytes);
            return null;
        }

        // Value of a `const string`, or of a `static readonly string` assigned from a literal in the
        // type initializer - Graphic_Single.MaskSuffix is the latter on this build (ldsfld, not ldstr).
        public string StaticString(string typeFullName, string fieldName)
        {
            string c = StringConstant(typeFullName, fieldName);
            if (c != null) return c;

            var fh = Field(typeFullName, fieldName);
            if (fh.IsNil) return null;
            int fieldToken = MetadataTokens.GetToken(fh);

            foreach (var mh in Methods(typeFullName, null))
            {
                var il = Body(mh);
                for (int i = 1; i < il.Count; i++)
                {
                    if (il[i].Name != "stsfld" || !il[i].HasToken) continue;
                    if ((il[i].Token & 0xFFFFFF) != (fieldToken & 0xFFFFFF)) continue;
                    for (int j = i - 1; j >= 0 && j >= i - 3; j--)
                        if (il[j].Name == "ldstr" && il[j].HasToken) return UserString(il[j].Token);
                }
            }
            return null;
        }

        // Root body plus the compiler-generated lambdas / local functions it creates, which is
        // where `x.name.Split('_')[0]` and the Graphic_Random type test actually live.
        public List<Insn> BodyWithClosures(MethodDefinitionHandle root)
        {
            var all = new List<Insn>();
            var seen = new HashSet<int>();
            Walk(root, 0);
            return all;

            void Walk(MethodDefinitionHandle h, int depth)
            {
                if (depth > 3 || !seen.Add(MetadataTokens.GetToken(h))) return;
                var il = Body(h);
                all.AddRange(il);
                foreach (var i in il)
                {
                    if (!i.HasToken || (i.Token >> 24) == 0x70) continue;
                    if (i.Name != "ldftn" && i.Name != "call" && i.Name != "callvirt") continue;
                    EntityHandle eh;
                    try { eh = MetadataTokens.EntityHandle(i.Token); } catch { continue; }
                    if (eh.Kind == HandleKind.MethodSpecification)
                        eh = Md.GetMethodSpecification((MethodSpecificationHandle)eh).Method;
                    if (eh.Kind != HandleKind.MethodDefinition) continue;
                    var mh = (MethodDefinitionHandle)eh;
                    string nm = Md.GetString(Md.GetMethodDefinition(mh).Name);
                    if (nm.Contains("b__") || nm.Contains("g__")) Walk(mh, depth + 1);
                }
            }
        }

        public string FieldType(string typeFullName, string fieldName)
        {
            var fh = Field(typeFullName, fieldName);
            if (fh.IsNil) return null;
            try { return Md.GetFieldDefinition(fh).DecodeSignature(Sig, null); }
            catch { return null; }
        }
    }

    sealed class Sig : ISignatureTypeProvider<string, object>
    {
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[]";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
            => genericType + "<" + string.Join(",", typeArguments) + ">";
        public string GetGenericMethodParameter(object genericContext, int index) => "!!" + index;
        public string GetGenericTypeParameter(object genericContext, int index) => "!" + index;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetPinnedType(string elementType) => elementType;
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            var td = reader.GetTypeDefinition(handle);
            string ns = reader.GetString(td.Namespace), n = reader.GetString(td.Name);
            return string.IsNullOrEmpty(ns) ? n : ns + "." + n;
        }
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var tr = reader.GetTypeReference(handle);
            string ns = reader.GetString(tr.Namespace), n = reader.GetString(tr.Name);
            return string.IsNullOrEmpty(ns) ? n : ns + "." + n;
        }
        public string GetTypeFromSpecification(MetadataReader reader, object genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
            => "typespec";
    }
}
