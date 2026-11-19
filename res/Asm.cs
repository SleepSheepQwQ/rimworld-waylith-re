using System;
using System.Collections.Generic;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Residency
{
    // Metadata-only view of one shipped assembly. Nothing here executes game code.
    internal sealed class Asm
    {
        public readonly PEReader Pe;
        public readonly MetadataReader Md;

        Asm(PEReader pe) { Pe = pe; Md = pe.GetMetadataReader(); }

        public static Asm Open(string path)
        {
            // keep the image seekable so GetMethodBody can read RVA data
            return new Asm(new PEReader(System.IO.File.OpenRead(path)));
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
                case HandleKind.TypeSpecification:
                    try { return Md.GetTypeSpecification((TypeSpecificationHandle)h).DecodeSignature(new Sig(), null); }
                    catch { return "typespec"; }
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
                case HandleKind.TypeSpecification:
                    try { return Md.GetTypeSpecification((TypeSpecificationHandle)h).DecodeSignature(new Sig(), null); }
                    catch { return "typespec"; }
                default: return "?";
            }
        }

        public IEnumerable<MethodDefinitionHandle> Methods(string typeFullName, string methodName)
        {
            foreach (var th in Md.TypeDefinitions)
            {
                if (TypeName(th) != typeFullName) continue;
                var td = Md.GetTypeDefinition(th);
                foreach (var mh in td.GetMethods())
                {
                    if (methodName != null && Md.GetString(Md.GetMethodDefinition(mh).Name) != methodName) continue;
                    yield return mh;
                }
            }
        }

        public MethodDefinitionHandle Method(string typeFullName, string methodName)
        {
            foreach (var mh in Methods(typeFullName, methodName)) return mh;
            throw new InvalidOperationException("method not found: " + typeFullName + "::" + methodName);
        }

        public List<Insn> Body(MethodDefinitionHandle h)
        {
            var md = Md.GetMethodDefinition(h);
            if (md.RelativeVirtualAddress == 0) return new List<Insn>();
            var block = Pe.GetMethodBody(md.RelativeVirtualAddress);
            return Il.Decode(block.GetILBytes());
        }

        public List<Insn> Body(string typeFullName, string methodName) => Body(Method(typeFullName, methodName));

        public bool HasField(string typeFullName, string fieldName)
        {
            foreach (var th in Md.TypeDefinitions)
            {
                if (TypeName(th) != typeFullName) continue;
                foreach (var fh in Md.GetTypeDefinition(th).GetFields())
                    if (Md.GetString(Md.GetFieldDefinition(fh).Name) == fieldName) return true;
            }
            return false;
        }

        sealed class Sig : ISignatureTypeProvider<string, object>
        {
            public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[]";
            public string GetByReferenceType(string elementType) => elementType + "&";
            public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
            public string GetGenericInstantiation(string genericType, System.Collections.Immutable.ImmutableArray<string> typeArguments)
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
}
