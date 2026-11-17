using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Host
{
    // One loaded managed module (dll) with its metadata reader and a name resolver.
    sealed class Module
    {
        public string File;
        public string Name;
        public PEReader Pe;
        public MetadataReader Md;
        public Namer Names;

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
                case HandleKind.TypeReference:
                    return RefName((TypeReferenceHandle)h);
                case HandleKind.TypeDefinition:
                    return TypeName((TypeDefinitionHandle)h);
                case HandleKind.TypeSpecification:
                    return SpecName((TypeSpecificationHandle)h);
                default:
                    return "?";
            }
        }

        public string ParentName(EntityHandle h)
        {
            switch (h.Kind)
            {
                case HandleKind.TypeReference: return RefName((TypeReferenceHandle)h);
                case HandleKind.TypeDefinition: return TypeName((TypeDefinitionHandle)h);
                case HandleKind.TypeSpecification: return SpecName((TypeSpecificationHandle)h);
                case HandleKind.MethodDefinition:
                    return TypeName(Md.GetMethodDefinition((MethodDefinitionHandle)h).GetDeclaringType());
                default: return "?";
            }
        }

        public string SpecName(TypeSpecificationHandle h)
        {
            try { return Md.GetTypeSpecification(h).DecodeSignature(Names, null); }
            catch { return "<typespec>"; }
        }

        public string TypeName(TypeDefinitionHandle h) => Namer.DefName(Md, h);

        public string RefName(TypeReferenceHandle h) => Namer.RefName(Md, h);

        // Full display name of a method, including parameter types.
        public string MethodSignature(MethodDefinitionHandle h)
        {
            var md = Md.GetMethodDefinition(h);
            string name = Md.GetString(md.Name);
            try
            {
                var sig = md.DecodeSignature(Names, null);
                var ps = sig.ParameterTypes.Select((p, i) => p + " " + ParamName(md, i));
                return TypeName(md.GetDeclaringType()) + "::" + name + "(" + string.Join(", ", ps) + ")";
            }
            catch
            {
                return TypeName(md.GetDeclaringType()) + "::" + name + "(" + md.GetParameters().Count + " params)";
            }
        }

        string ParamName(MethodDefinition md, int index)
        {
            try
            {
                int n = 0;
                foreach (var ph in md.GetParameters())
                {
                    if (n == index)
                    {
                        var p = Md.GetParameter(ph);
                        string nm = Md.GetString(p.Name);
                        if (!string.IsNullOrEmpty(nm)) return nm;
                        break;
                    }
                    n++;
                }
            }
            catch { }
            return "arg" + index;
        }

        public string UserString(int token)
        {
            try
            {
                var h = MetadataTokens.UserStringHandle(token & 0xFFFFFF);
                return Md.GetUserString(h);
            }
            catch { return null; }
        }
    }

    // Reads every managed dll in a directory and answers name/IL questions about them.
    static class Meta
    {
        public static List<Module> Load(string dir)
        {
            var list = new List<Module>();
            foreach (var path in Directory.GetFiles(dir, "*.dll").OrderBy(p => p, StringComparer.Ordinal))
            {
                PEReader pe = null;
                try
                {
                    pe = new PEReader(File.OpenRead(path));
                    if (!pe.HasMetadata) { pe.Dispose(); continue; }
                    var md = pe.GetMetadataReader();
                    string name = Path.GetFileNameWithoutExtension(path);
                    try
                    {
                        if (!md.IsAssembly) { } // netmodule: keep file name
                        else name = md.GetString(md.GetAssemblyDefinition().Name);
                    }
                    catch { }
                    var m = new Module { File = path, Name = name, Pe = pe, Md = md };
                    m.Names = new Namer(md);
                    list.Add(m);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  (skipped " + Path.GetFileName(path) + ": " + ex.Message + ")");
                    if (pe != null) pe.Dispose();
                }
            }
            return list;
        }

        public static IEnumerable<MethodDefinitionHandle> Methods(Module m, string typeFullName, string methodName)
        {
            foreach (var th in m.Md.TypeDefinitions)
            {
                if (typeFullName != null && m.TypeName(th) != typeFullName) continue;
                var td = m.Md.GetTypeDefinition(th);
                foreach (var mh in td.GetMethods())
                {
                    if (methodName != null && m.Md.GetString(m.Md.GetMethodDefinition(mh).Name) != methodName) continue;
                    yield return mh;
                }
            }
        }

        public static bool TryBody(Module m, MethodDefinitionHandle h, out byte[] il, out string error)
        {
            il = null;
            error = null;
            var md = m.Md.GetMethodDefinition(h);
            if (md.RelativeVirtualAddress == 0) { error = "no body (abstract / extern)"; return false; }
            try
            {
                var body = m.Pe.GetMethodBody(md.RelativeVirtualAddress);
                il = body.GetILBytes();
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        // Integer constants of an enum type, by simple name (e.g. EventType.Repaint -> 7).
        public static Dictionary<long, string> EnumValues(List<Module> mods, string simpleTypeName)
        {
            var d = new Dictionary<long, string>();
            foreach (var m in mods)
            {
                foreach (var th in m.Md.TypeDefinitions)
                {
                    var td = m.Md.GetTypeDefinition(th);
                    if (m.Md.GetString(td.Name) != simpleTypeName) continue;
                    foreach (var fh in td.GetFields())
                    {
                        var fd = m.Md.GetFieldDefinition(fh);
                        var ch = fd.GetDefaultValue();
                        if (ch.IsNil) continue;
                        var c = m.Md.GetConstant(ch);
                        var bytes = m.Md.GetBlobBytes(c.Value);
                        if (bytes.Length == 0 || bytes.Length > 8) continue;
                        long v = 0;
                        for (int i = bytes.Length - 1; i >= 0; i--) v = (v << 8) | bytes[i];
                        string nm = m.Md.GetString(fd.Name);
                        if (!d.ContainsKey(v)) d[v] = nm;
                    }
                }
            }
            return d;
        }
    }

    // Resolves metadata type names to readable strings.
    sealed class Namer : ISignatureTypeProvider<string, object>
    {
        readonly MetadataReader R;
        public Namer(MetadataReader r) { R = r; }

        public static string DefName(MetadataReader r, TypeDefinitionHandle h)
        {
            var td = r.GetTypeDefinition(h);
            string name = r.GetString(td.Name);
            var decl = td.GetDeclaringType();
            if (!decl.IsNil) return DefName(r, decl) + "/" + name;
            string ns = r.GetString(td.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        public static string RefName(MetadataReader r, TypeReferenceHandle h)
        {
            var tr = r.GetTypeReference(h);
            string name = r.GetString(tr.Name);
            var scope = tr.ResolutionScope;
            if (scope.Kind == HandleKind.TypeReference) return RefName(r, (TypeReferenceHandle)scope) + "/" + name;
            string ns = r.GetString(tr.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString().ToLowerInvariant();

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
            => DefName(reader, handle);

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
            => RefName(reader, handle);

        public string GetTypeFromSpecification(MetadataReader reader, object genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        {
            try { return reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext); }
            catch { return "<typespec>"; }
        }

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
            => genericType + "<" + string.Join(", ", typeArguments) + ">";

        public string GetArrayType(string elementType, ArrayShape shape)
            => elementType + "[" + new string(',', Math.Max(0, shape.Rank - 1)) + "]";

        public string GetByReferenceType(string elementType) => "ref " + elementType;
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
        public string GetGenericMethodParameter(object genericContext, int index) => "!!" + index;
        public string GetGenericTypeParameter(object genericContext, int index) => "!" + index;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetPinnedType(string elementType) => elementType;
    }

    enum OpKind { None, I1, I4, Var2, I8, R4, R8, Token, Switch }

    sealed class Insn
    {
        public int Offset;
        public int Size;
        public int Op;
        public int Op2;
        public bool Prefixed;
        public string OpName;
        public int Token;
        public bool HasToken;
        public long Imm;
        public bool HasImm;
        public int BranchTarget = -1;
    }

    // A small, complete ECMA-335 IL decoder. Only operands are decoded; that is all the
    // assertions need, and it keeps the walk exact (no byte-pattern guessing).
    static class Il
    {
        static readonly Dictionary<int, OpKind> Kinds = BuildKinds();
        static readonly Dictionary<int, OpKind> FeKinds = BuildFeKinds();

        static Dictionary<int, OpKind> BuildKinds()
        {
            var d = new Dictionary<int, OpKind>();
            void N(params int[] ops) { foreach (var o in ops) d[o] = OpKind.None; }
            void K(OpKind k, params int[] ops) { foreach (var o in ops) d[o] = k; }

            N(0x00, 0x01);
            N(0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D);
            K(OpKind.I1, 0x0E, 0x0F, 0x10, 0x11, 0x12, 0x13);
            N(0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E);
            K(OpKind.I1, 0x1F);
            K(OpKind.I4, 0x20);
            K(OpKind.I8, 0x21);
            K(OpKind.R4, 0x22);
            K(OpKind.R8, 0x23);
            N(0x25, 0x26);
            K(OpKind.Token, 0x27);
            K(OpKind.Token, 0x28, 0x29);
            N(0x2A);
            K(OpKind.I1, 0x2B, 0x2C, 0x2D, 0x2E, 0x2F, 0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37);
            K(OpKind.I4, 0x38, 0x39, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x43, 0x44);
            K(OpKind.Switch, 0x45);
            N(0x46, 0x47, 0x48, 0x49, 0x4A, 0x4B, 0x4C, 0x4D, 0x4E, 0x4F, 0x50, 0x51, 0x52,
              0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x5B, 0x5C, 0x5D, 0x5E, 0x5F,
              0x60, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x6B, 0x6C,
              0x6D, 0x6E);
            K(OpKind.Token, 0x6F, 0x70, 0x71, 0x72, 0x73, 0x74, 0x75);
            N(0x76);
            K(OpKind.Token, 0x79);
            N(0x7A);
            K(OpKind.Token, 0x7B, 0x7C, 0x7D, 0x7E, 0x7F, 0x80, 0x81);
            N(0x82, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89, 0x8A, 0x8B);
            K(OpKind.Token, 0x8C, 0x8D);
            N(0x8E);
            K(OpKind.Token, 0x8F);
            N(0x90, 0x91, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A);
            N(0x9B, 0x9C, 0x9D, 0x9E, 0x9F, 0xA0, 0xA1, 0xA2);
            K(OpKind.Token, 0xA3, 0xA4, 0xA5);
            N(0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA);
            K(OpKind.Token, 0xC2);
            N(0xC3);
            K(OpKind.Token, 0xC6);
            K(OpKind.Token, 0xD0);
            N(0xD1, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xDB, 0xDC);
            K(OpKind.I4, 0xDD);
            K(OpKind.I1, 0xDE);
            N(0xDF, 0xE0);
            return d;
        }

        static Dictionary<int, OpKind> BuildFeKinds()
        {
            var d = new Dictionary<int, OpKind>();
            void N(params int[] ops) { foreach (var o in ops) d[o] = OpKind.None; }
            void K(OpKind k, params int[] ops) { foreach (var o in ops) d[o] = k; }
            N(0x00, 0x01, 0x02, 0x03, 0x04, 0x05);
            K(OpKind.Token, 0x06, 0x07);
            K(OpKind.Var2, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E);
            N(0x0F);
            N(0x11);
            K(OpKind.I1, 0x12);
            N(0x13, 0x14);
            K(OpKind.Token, 0x15, 0x16);
            N(0x17, 0x18);
            K(OpKind.I1, 0x19);
            N(0x1A);
            K(OpKind.Token, 0x1C);
            N(0x1D, 0x1E);
            return d;
        }

        public static List<Insn> Decode(byte[] il, out string error)
        {
            error = null;
            var list = new List<Insn>();
            try
            {
                int i = 0;
                while (i < il.Length)
                {
                    var ins = new Insn { Offset = i };
                    int op = il[i++];
                    OpKind k;
                    if (op == 0xFE)
                    {
                        int op2 = il[i++];
                        if (!FeKinds.TryGetValue(op2, out k))
                        {
                            error = "unknown opcode FE" + op2.ToString("X2") + " at IL_" + ins.Offset.ToString("X4");
                            return null;
                        }
                        ins.Prefixed = true; ins.Op = 0xFE; ins.Op2 = op2;
                        ins.OpName = "fe" + op2.ToString("X2");
                    }
                    else
                    {
                        if (!Kinds.TryGetValue(op, out k))
                        {
                            error = "unknown opcode " + op.ToString("X2") + " at IL_" + ins.Offset.ToString("X4");
                            return null;
                        }
                        ins.Op = op;
                        ins.OpName = NameOf(op);
                    }

                    switch (k)
                    {
                        case OpKind.None: break;
                        case OpKind.I1: ins.Imm = (sbyte)il[i]; ins.HasImm = true; i += 1; break;
                        case OpKind.Var2: i += 2; break;
                        case OpKind.I4: ins.Imm = BitConverter.ToInt32(il, i); ins.HasImm = true; i += 4; break;
                        case OpKind.R4: i += 4; break;
                        case OpKind.I8:
                        case OpKind.R8: i += 8; break;
                        case OpKind.Token: ins.Token = BitConverter.ToInt32(il, i); ins.HasToken = true; i += 4; break;
                        case OpKind.Switch:
                        {
                            int n = BitConverter.ToInt32(il, i);
                            i += 4 + 4 * n;
                            break;
                        }
                    }
                    ins.Size = i - ins.Offset;
                    if (ins.Size <= 0) { error = "zero-size instruction at IL_" + ins.Offset.ToString("X4"); return null; }
                    if (IsBranch(ins)) ins.BranchTarget = ins.Offset + ins.Size + (int)ins.Imm;
                    list.Add(ins);
                }
            }
            catch (IndexOutOfRangeException)
            {
                error = "truncated operand at IL_" + (list.Count > 0 ? list[list.Count - 1].Offset : 0).ToString("X4");
                return null;
            }
            catch (Exception ex)
            {
                error = "IL decode failed: " + ex.Message;
                return null;
            }
            return list;
        }

        public static bool IsBranch(Insn ins)
        {
            if (ins.Prefixed) return false;
            int o = ins.Op;
            return (o >= 0x2B && o <= 0x44) || o == 0xDD || o == 0xDE;
        }

        public static bool IsCall(Insn ins)
        {
            if (!ins.HasToken) return false;
            if (ins.Prefixed) return ins.Op2 == 0x06 || ins.Op2 == 0x07;   // ldftn / ldvirtftn
            return ins.Op == 0x28 || ins.Op == 0x6F || ins.Op == 0x73 || ins.Op == 0x27;
        }

        public static bool TryLdcI4(Insn ins, out long value)
        {
            value = 0;
            if (ins.Prefixed) return false;
            if (ins.Op >= 0x16 && ins.Op <= 0x1E) { value = ins.Op - 0x16; return true; }
            if (ins.Op == 0x15) { value = -1; return true; }
            if ((ins.Op == 0x1F || ins.Op == 0x20) && ins.HasImm) { value = ins.Imm; return true; }
            return false;
        }

        public static bool IsCompare(Insn ins)
            => ins.Prefixed ? (ins.Op2 == 0x01 || ins.Op2 == 0x02 || ins.Op2 == 0x03 || ins.Op2 == 0x04 || ins.Op2 == 0x05) : false;

        static string NameOf(int op)
        {
            switch (op)
            {
                case 0x00: return "nop";
                case 0x02: return "ldarg.0";
                case 0x03: return "ldarg.1";
                case 0x04: return "ldarg.2";
                case 0x05: return "ldarg.3";
                case 0x0E: return "ldarg.s";
                case 0x14: return "ldnull";
                case 0x15: return "ldc.i4.m1";
                case 0x16: return "ldc.i4.0";
                case 0x1B: return "ldc.i4.5";
                case 0x1C: return "ldc.i4.6";
                case 0x1D: return "ldc.i4.7";
                case 0x1E: return "ldc.i4.8";
                case 0x20: return "ldc.i4";
                case 0x25: return "dup";
                case 0x26: return "pop";
                case 0x28: return "call";
                case 0x2A: return "ret";
                case 0x2B: return "br.s";
                case 0x2C: return "brfalse.s";
                case 0x2D: return "brtrue.s";
                case 0x38: return "br";
                case 0x39: return "brfalse";
                case 0x3A: return "brtrue";
                case 0x6F: return "callvirt";
                case 0x72: return "ldstr";
                case 0x73: return "newobj";
                case 0x74: return "castclass";
                case 0x75: return "isinst";
                case 0x7B: return "ldfld";
                case 0x7D: return "stfld";
                case 0x7E: return "ldsfld";
                case 0x80: return "stsfld";
                case 0xDD: return "leave";
                case 0xDE: return "leave.s";
                default: return "op" + op.ToString("X2");
            }
        }
    }
}
