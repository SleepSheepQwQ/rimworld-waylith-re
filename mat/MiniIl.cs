using System;
using System.Collections.Generic;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace MatPool
{
    // A deliberately small ECMA-335 reader: enough to walk a method body and answer
    // "does this method contain call X / literal Y / opcode Z, in this order?". Written for
    // flow/ so the harness does not depend on the host/ assertion suite's internals.

    enum Operand { None, I1, I4, I8, R4, R8, Token, Var2, Switch }

    sealed class Insn
    {
        public int Offset;
        public bool Prefixed;
        public int Op;
        public string Name;
        public int Token;
        public long Imm;
        public bool HasToken;
        public bool HasImm;
        public int Target = -1;     // branch destination, when this is a branch

        public override string ToString()
        {
            string s = Name;
            if (HasImm) s += " " + Imm;
            if (HasToken) s += " tok=0x" + Token.ToString("X8");
            if (Target >= 0) s += " -> IL_" + Target.ToString("X4");
            return s;
        }
    }

    static class MiniIl
    {
        public static List<Insn> Decode(byte[] il)
        {
            var list = new List<Insn>(il.Length / 2);
            int i = 0;
            while (i < il.Length)
            {
                var ins = new Insn { Offset = i };
                int op = il[i++];
                Operand kind;
                if (op == 0xFE)
                {
                    if (i >= il.Length) throw new BadImageFormatException("truncated 0xFE prefix");
                    int op2 = il[i++];
                    ins.Prefixed = true;
                    ins.Op = 0xFE00 | op2;
                    ins.Name = Name2(op2);
                    kind = Kind2(op2);
                }
                else
                {
                    ins.Op = op;
                    ins.Name = Name1(op);
                    kind = Kind1(op);
                }

                switch (kind)
                {
                    case Operand.None: break;
                    case Operand.I1: ins.Imm = (sbyte)il[i]; ins.HasImm = true; i += 1; break;
                    case Operand.Var2: i += 2; break;
                    case Operand.I4: ins.Imm = BitConverter.ToInt32(il, i); ins.HasImm = true; i += 4; break;
                    case Operand.R4: i += 4; break;
                    case Operand.I8:
                    case Operand.R8: i += 8; break;
                    case Operand.Token: ins.Token = BitConverter.ToInt32(il, i); ins.HasToken = true; i += 4; break;
                    case Operand.Switch:
                    {
                        int n = BitConverter.ToInt32(il, i);
                        i += 4 + 4 * n;
                        break;
                    }
                }

                int size = i - ins.Offset;
                if (size <= 0) throw new BadImageFormatException("zero-size instruction at IL_" + ins.Offset.ToString("X4"));
                if (IsBranch(ins)) ins.Target = ins.Offset + size + (int)ins.Imm;
                list.Add(ins);
            }
            return list;
        }

        public static bool IsBranch(Insn ins)
        {
            if (ins.Prefixed) return false;
            return (ins.Op >= 0x2B && ins.Op <= 0x44) || ins.Op == 0xDD || ins.Op == 0xDE;
        }

        public static bool IsCall(Insn ins) => ins.HasToken && (ins.Op == 0x28 || ins.Op == 0x6F || ins.Op == 0x73);

        static Operand Kind1(int op)
        {
            if (op <= 0x0D) return Operand.None;
            if (op <= 0x13) return Operand.I1;                      // ldarg.s .. stloc.s
            if (op <= 0x1E) return Operand.None;                    // ldnull, ldc.i4.m1..8
            switch (op)
            {
                case 0x1F: return Operand.I1;                        // ldc.i4.s
                case 0x20: return Operand.I4;                        // ldc.i4
                case 0x21: return Operand.I8;
                case 0x22: return Operand.R4;
                case 0x23: return Operand.R8;
                case 0x27: case 0x28: case 0x29: return Operand.Token;
                case 0x45: return Operand.Switch;
                case 0x6F: case 0x70: case 0x71: case 0x72:
                case 0x73: case 0x74: case 0x75: return Operand.Token;
                case 0x79: return Operand.Token;
                case 0x7B: case 0x7C: case 0x7D: case 0x7E:
                case 0x7F: case 0x80: case 0x81: return Operand.Token;
                case 0x8C: case 0x8D: case 0x8F: return Operand.Token;
                case 0xA3: case 0xA4: case 0xA5: return Operand.Token;
                case 0xC2: case 0xC6: return Operand.Token;
                case 0xD0: return Operand.Token;
                case 0xDD: return Operand.I4;                         // leave
                case 0xDE: return Operand.I1;                         // leave.s
            }
            if (op >= 0x2B && op <= 0x37) return Operand.I1;          // short branches
            if (op >= 0x38 && op <= 0x44) return Operand.I4;           // long branches
            return Operand.None;                                      // arith / conv / ind / element ops
        }

        static Operand Kind2(int op)
        {
            switch (op)
            {
                case 0x06: case 0x07: return Operand.Token;            // ldftn / ldvirtftn
                case 0x09: case 0x0A: case 0x0B: case 0x0C: case 0x0D: case 0x0E: return Operand.Var2;
                case 0x12: return Operand.I1;
                case 0x15: case 0x16: return Operand.Token;            // initobj / constrained.
                case 0x19: return Operand.I1;
                case 0x1C: return Operand.Token;
                default: return Operand.None;
            }
        }

        static string Name1(int op)
        {
            switch (op)
            {
                case 0x00: return "nop";
                case 0x01: return "break";
                case 0x02: return "ldarg.0";
                case 0x03: return "ldarg.1";
                case 0x04: return "ldarg.2";
                case 0x05: return "ldarg.3";
                case 0x06: return "ldloc.0";
                case 0x07: return "ldloc.1";
                case 0x08: return "ldloc.2";
                case 0x09: return "ldloc.3";
                case 0x0A: return "stloc.0";
                case 0x0B: return "stloc.1";
                case 0x0C: return "stloc.2";
                case 0x0D: return "stloc.3";
                case 0x0E: return "ldarg.s";
                case 0x0F: return "ldarga.s";
                case 0x10: return "starg.s";
                case 0x11: return "ldloc.s";
                case 0x12: return "ldloca.s";
                case 0x13: return "stloc.s";
                case 0x14: return "ldnull";
                case 0x15: return "ldc.i4.m1";
                case 0x16: return "ldc.i4.0";
                case 0x17: return "ldc.i4.1";
                case 0x18: return "ldc.i4.2";
                case 0x19: return "ldc.i4.3";
                case 0x1A: return "ldc.i4.4";
                case 0x1B: return "ldc.i4.5";
                case 0x1C: return "ldc.i4.6";
                case 0x1D: return "ldc.i4.7";
                case 0x1E: return "ldc.i4.8";
                case 0x1F: return "ldc.i4.s";
                case 0x20: return "ldc.i4";
                case 0x21: return "ldc.i8";
                case 0x25: return "dup";
                case 0x26: return "pop";
                case 0x27: return "jmp";
                case 0x28: return "call";
                case 0x29: return "calli";
                case 0x2A: return "ret";
                case 0x2B: return "brtrue.s";
                case 0x2C: return "brfalse.s";
                case 0x2D: return "beq.s";
                case 0x2E: return "bge.s";
                case 0x2F: return "bgt.s";
                case 0x30: return "ble.s";
                case 0x31: return "blt.s";
                case 0x33: return "bne.un.s";
                case 0x38: return "br";
                case 0x39: return "brfalse";
                case 0x3A: return "brtrue";
                case 0x3C: return "blt";
                case 0x3E: return "ble";
                case 0x45: return "switch";
                case 0x58: return "add";
                case 0x59: return "sub";
                case 0x5A: return "mul";
                case 0x5B: return "div";
                case 0x5D: return "rem";
                case 0x69: return "conv.i4";
                case 0x6F: return "callvirt";
                case 0x72: return "ldstr";
                case 0x73: return "newobj";
                case 0x74: return "castclass";
                case 0x75: return "isinst";
                case 0x7B: return "ldfld";
                case 0x7D: return "stfld";
                case 0x7E: return "ldsfld";
                case 0x7F: return "ldsflda";
                case 0x80: return "stsfld";
                case 0x8C: return "box";
                case 0x8D: return "newarr";
                case 0x8E: return "ldlen";
                case 0x9A: return "ldelem.ref";
                case 0xA2: return "stelem.ref";
                case 0xC6: return "mkrefany";
                case 0xD0: return "ldtoken";
                case 0xDC: return "endfinally";
                case 0xDD: return "leave";
                case 0xDE: return "leave.s";
                case 0x7A: return "throw";
                default: return "op" + op.ToString("X2");
            }
        }

        static string Name2(int op)
        {
            switch (op)
            {
                case 0x01: return "ceq";
                case 0x02: return "cgt";
                case 0x03: return "cgt.un";
                case 0x04: return "clt";
                case 0x05: return "clt.un";
                case 0x06: return "ldftn";
                case 0x07: return "ldvirtftn";
                case 0x15: return "initobj";
                case 0x16: return "constrained.";
                default: return "fe" + op.ToString("X2");
            }
        }
    }
}
