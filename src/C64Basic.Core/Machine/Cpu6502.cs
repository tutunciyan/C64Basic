namespace C64Basic.Core.Machine;

/// <summary>Why <see cref="Cpu6502.Call"/> stopped.</summary>
public enum CpuStop
{
    None,
    /// <summary>The routine executed RTS back to its caller.</summary>
    Returned,
    /// <summary>BRK with the default KERNAL vector: the machine would drop back to BASIC.</summary>
    Break,
    /// <summary>An undocumented opcode, which is not implemented.</summary>
    IllegalOpcode,
    /// <summary>Execution reached a KERNAL or BASIC ROM address that has no emulation (there is no ROM image).</summary>
    NoRom,
    /// <summary><see cref="Cpu6502.Tick"/> asked to stop (RUN/STOP pressed).</summary>
    Interrupted,
    /// <summary>A trap ended the call.</summary>
    Terminated,
}

/// <summary>What the CPU does after a trap has run in place of the code at its address.</summary>
public enum TrapResult { Return, Continue, Stop }

/// <summary>
/// An NMOS 6502: all 151 documented opcodes with cycle counts (page-crossing and branch penalties), decimal mode,
/// read-modify-write dummy writes and IRQ/BRK sequencing. Memory goes through the <see cref="Bus"/>. The C64 ROMs are
/// not present: <see cref="Traps"/> replace KERNAL and BASIC entry points with C# code.
/// </summary>
public sealed class Cpu6502
{
    public const int FlagC = 1, FlagZ = 2, FlagI = 4, FlagD = 8, FlagB = 16, FlagU = 32, FlagV = 64, FlagN = 128;

    /// <summary>Address a called routine returns to; reaching it ends <see cref="Call"/>.</summary>
    public const int ReturnAddress = 0xFFFF;

    /// <summary>Cycles between <see cref="Tick"/> calls and between interrupt-line polls.</summary>
    const int TickInterval = 1024, IrqPollInterval = 32;

    readonly Bus _bus;

    public byte A, X, Y, SP = 0xFF, P = FlagU;
    public int PC;
    public long Cycles;
    public CpuStop StopReason { get; private set; }

    /// <summary>Code that runs instead of the instruction at an address, typically a KERNAL routine.</summary>
    public Dictionary<int, Func<Cpu6502, TrapResult>> Traps { get; } = new();

    /// <summary>Called every ~1000 cycles with the total cycle count; return true to stop the program.</summary>
    public Func<long, bool>? Tick { get; set; }

    /// <summary>Called when the default KERNAL interrupt handler runs (the 60 Hz system IRQ).</summary>
    public Action? SystemIrq { get; set; }

    public Cpu6502(Bus bus) => _bus = bus;

    // ---------- helpers ----------
    int Rd(int address) => _bus.Read(address & 0xFFFF);
    void Wr(int address, int value) => _bus.Write(address & 0xFFFF, (byte)value);
    int Fetch() { int v = Rd(PC); PC = (PC + 1) & 0xFFFF; return v; }
    int Word(int address) => Rd(address) | Rd(address + 1) << 8;

    public void Push(int v) { Wr(0x100 + SP, v); SP--; }
    public int Pull() { SP++; return Rd(0x100 + SP); }

    public bool GetFlag(int flag) => (P & flag) != 0;
    public void SetFlag(int flag, bool on) { if (on) P |= (byte)flag; else P &= (byte)~flag; }
    public void SetNZ(int v) { SetFlag(FlagZ, (v & 0xFF) == 0); SetFlag(FlagN, (v & 0x80) != 0); }

    /// <summary>True while the KERNAL ROM would be mapped in at 57344-65535.</summary>
    bool KernalVisible => (_bus.Ram[1] & 2) != 0;
    bool BasicVisible => (_bus.Ram[1] & 3) == 3;

    bool InRom(int pc) => pc >= 0xE000 ? KernalVisible : pc >= 0xA000 && pc < 0xC000 && BasicVisible;

    // ---------- running ----------
    /// <summary>Runs the routine at <paramref name="address"/> as if JSR'd, until it returns, stops or is interrupted.</summary>
    public CpuStop Call(int address)
    {
        StopReason = CpuStop.None;
        byte savedSp = SP;
        Push((ReturnAddress - 1) >> 8);
        Push((ReturnAddress - 1) & 0xFF);
        PC = address & 0xFFFF;

        long nextTick = Cycles + TickInterval, nextIrq = Cycles + IrqPollInterval;
        try
        {
            while (StopReason == CpuStop.None)
            {
                if (PC == ReturnAddress) { StopReason = CpuStop.Returned; break; }
                if (Cycles >= nextIrq)
                {
                    nextIrq = Cycles + IrqPollInterval;
                    if (!GetFlag(FlagI) && _bus.IrqLine) Irq();
                }
                Step();
                if (Cycles >= nextTick)
                {
                    nextTick = Cycles + TickInterval;
                    if (Tick?.Invoke(Cycles) == true) StopReason = CpuStop.Interrupted;
                }
            }
        }
        finally { SP = savedSp; }
        return StopReason;
    }

    /// <summary>Executes one instruction (or one trap).</summary>
    public void Step()
    {
        if (Traps.Count > 0 && Traps.TryGetValue(PC, out var trap))
        {
            Cycles += 6;
            switch (trap(this))
            {
                case TrapResult.Return: Rts(); break;
                case TrapResult.Stop: StopReason = CpuStop.Terminated; break;
            }
            return;
        }
        if (InRom(PC)) { StopReason = CpuStop.NoRom; return; }

        int op = Fetch();
        int cycles = BaseCycles[op];
        if (cycles == 0) { StopReason = CpuStop.IllegalOpcode; PC = (PC - 1) & 0xFFFF; return; }
        Cycles += cycles;
        Execute(op);
    }

    // ---------- interrupts ----------
    void Irq()
    {
        if (KernalVisible)
        {
            int vector = Word(0x314);
            if (vector == 0xEA31) { AcknowledgeSystemIrq(); Cycles += 40; return; } // the stock handler: no frame needed
            Push(PC >> 8); Push(PC & 0xFF); Push((P & ~FlagB) | FlagU);
            SetFlag(FlagI, true);
            Push(A); Push(X); Push(Y); // what the ROM entry does before jumping through $0314
            PC = vector;
        }
        else
        {
            int vector = Word(0xFFFE);
            if (vector == 0) { _bus.Read(0xDC0D); return; } // no handler installed: ignore rather than run at 0
            Push(PC >> 8); Push(PC & 0xFF); Push((P & ~FlagB) | FlagU);
            SetFlag(FlagI, true);
            PC = vector;
        }
        Cycles += 7;
    }

    /// <summary>Acknowledges CIA 1 and ticks the jiffy clock, as the ROM's $EA31 does.</summary>
    public void AcknowledgeSystemIrq()
    {
        _bus.Read(0xDC0D);
        SystemIrq?.Invoke();
    }

    /// <summary>The tail of the ROM interrupt handler ($EA81): pull Y, X, A and return from the interrupt.</summary>
    public void LeaveInterrupt()
    {
        Y = (byte)Pull(); X = (byte)Pull(); A = (byte)Pull();
        Rti();
    }

    void Rti()
    {
        P = (byte)((Pull() & ~FlagB) | FlagU);
        int lo = Pull(), hi = Pull();
        PC = lo | hi << 8;
    }

    void Rts()
    {
        int lo = Pull(), hi = Pull();
        PC = ((lo | hi << 8) + 1) & 0xFFFF;
    }

    void Brk()
    {
        Fetch(); // BRK is followed by a padding byte
        Push(PC >> 8); Push(PC & 0xFF); Push(P | FlagB | FlagU);
        SetFlag(FlagI, true);
        if (KernalVisible)
        {
            int vector = Word(0x316);
            if (vector == 0xFE66) { StopReason = CpuStop.Break; return; }
            Push(A); Push(X); Push(Y);
            PC = vector;
        }
        else
        {
            int vector = Word(0xFFFE);
            if (vector == 0) { StopReason = CpuStop.Break; return; }
            PC = vector;
        }
    }

    // ---------- addressing ----------
    int ZeroPage() => Fetch();
    int ZeroPageX() => (Fetch() + X) & 0xFF;
    int ZeroPageY() => (Fetch() + Y) & 0xFF;
    int Absolute() { int lo = Fetch(); return lo | Fetch() << 8; }

    int AbsoluteIndexed(int index, bool penalty)
    {
        int b = Absolute(), a = (b + index) & 0xFFFF;
        if (penalty && (a & 0xFF00) != (b & 0xFF00)) Cycles++;
        return a;
    }

    int IndirectX()
    {
        int z = (Fetch() + X) & 0xFF;
        return Rd(z) | Rd((z + 1) & 0xFF) << 8;
    }

    int IndirectY(bool penalty)
    {
        int z = Fetch();
        int b = Rd(z) | Rd((z + 1) & 0xFF) << 8, a = (b + Y) & 0xFFFF;
        if (penalty && (a & 0xFF00) != (b & 0xFF00)) Cycles++;
        return a;
    }

    // ---------- operations ----------
    void Adc(int m)
    {
        int c = P & FlagC;
        int binary = A + m + c;
        if (!GetFlag(FlagD))
        {
            SetFlag(FlagV, (~(A ^ m) & (A ^ binary) & 0x80) != 0);
            SetFlag(FlagC, binary > 0xFF);
            A = (byte)binary;
            SetNZ(A);
            return;
        }
        int lo = (A & 0x0F) + (m & 0x0F) + c;
        if (lo >= 0x0A) lo = ((lo + 0x06) & 0x0F) + 0x10;
        int sum = (A & 0xF0) + (m & 0xF0) + lo;
        SetFlag(FlagN, (sum & 0x80) != 0);
        SetFlag(FlagV, (~(A ^ m) & (A ^ sum) & 0x80) != 0);
        SetFlag(FlagZ, (binary & 0xFF) == 0); // NMOS: Z comes from the binary result
        if (sum >= 0xA0) sum += 0x60;
        SetFlag(FlagC, sum >= 0x100);
        A = (byte)sum;
    }

    void Sbc(int m)
    {
        int borrow = 1 - (P & FlagC);
        int binary = A - m - borrow;
        SetFlag(FlagV, ((A ^ m) & (A ^ binary) & 0x80) != 0);
        SetFlag(FlagC, binary >= 0);
        if (!GetFlag(FlagD))
        {
            A = (byte)binary;
            SetNZ(A);
            return;
        }
        SetNZ(binary); // NMOS: all flags come from the binary result
        int lo = (A & 0x0F) - (m & 0x0F) - borrow;
        if (lo < 0) lo = ((lo - 0x06) & 0x0F) - 0x10;
        int result = (A & 0xF0) - (m & 0xF0) + lo;
        if (result < 0) result -= 0x60;
        A = (byte)result;
    }

    void Compare(int reg, int m)
    {
        int r = reg - m;
        SetFlag(FlagC, r >= 0);
        SetNZ(r);
    }

    int Asl(int v) { SetFlag(FlagC, (v & 0x80) != 0); v = (v << 1) & 0xFF; SetNZ(v); return v; }
    int Lsr(int v) { SetFlag(FlagC, (v & 1) != 0); v >>= 1; SetNZ(v); return v; }
    int Rol(int v) { int c = P & FlagC; SetFlag(FlagC, (v & 0x80) != 0); v = (v << 1 | c) & 0xFF; SetNZ(v); return v; }
    int Ror(int v) { int c = P & FlagC; SetFlag(FlagC, (v & 1) != 0); v = v >> 1 | c << 7; SetNZ(v); return v; }

    /// <summary>Read-modify-write: the real chip writes the old value back before the new one, which matters for I/O registers.</summary>
    void Modify(int address, Func<int, int> op)
    {
        int old = Rd(address);
        Wr(address, old);
        Wr(address, op(old));
    }

    void Branch(int op)
    {
        int offset = (sbyte)Fetch();
        bool value = (op & 0x20) != 0;
        bool flag = (op >> 6 & 3) switch
        {
            0 => GetFlag(FlagN),
            1 => GetFlag(FlagV),
            2 => GetFlag(FlagC),
            _ => GetFlag(FlagZ),
        };
        if (flag != value) return;
        Cycles++;
        int target = (PC + offset) & 0xFFFF;
        if ((target & 0xFF00) != (PC & 0xFF00)) Cycles++;
        PC = target;
    }

    void Execute(int op)
    {
        if ((op & 0x1F) == 0x10) { Branch(op); return; }

        switch (op)
        {
            case 0x00: Brk(); return;
            case 0x20: { int a = Absolute(); int ret = (PC - 1) & 0xFFFF; Push(ret >> 8); Push(ret & 0xFF); PC = a; return; }
            case 0x40: Rti(); return;
            case 0x60: Rts(); return;
            case 0x4C: PC = Absolute(); return;
            case 0x6C:
                {
                    int p = Absolute();
                    // the NMOS chip does not carry into the high byte when the pointer sits at the end of a page
                    PC = Rd(p) | Rd(p & 0xFF00 | (p + 1) & 0xFF) << 8;
                    return;
                }
            case 0x08: Push(P | FlagB | FlagU); return;
            case 0x28: P = (byte)((Pull() & ~FlagB) | FlagU); return;
            case 0x48: Push(A); return;
            case 0x68: A = (byte)Pull(); SetNZ(A); return;
            case 0x88: Y--; SetNZ(Y); return;
            case 0xA8: Y = A; SetNZ(Y); return;
            case 0xC8: Y++; SetNZ(Y); return;
            case 0xE8: X++; SetNZ(X); return;
            case 0xCA: X--; SetNZ(X); return;
            case 0xAA: X = A; SetNZ(X); return;
            case 0x8A: A = X; SetNZ(A); return;
            case 0x98: A = Y; SetNZ(A); return;
            case 0xBA: X = SP; SetNZ(X); return;
            case 0x9A: SP = X; return;
            case 0x18: SetFlag(FlagC, false); return;
            case 0x38: SetFlag(FlagC, true); return;
            case 0x58: SetFlag(FlagI, false); return;
            case 0x78: SetFlag(FlagI, true); return;
            case 0xB8: SetFlag(FlagV, false); return;
            case 0xD8: SetFlag(FlagD, false); return;
            case 0xF8: SetFlag(FlagD, true); return;
            case 0xEA: return;
            case 0x0A: A = (byte)Asl(A); return;
            case 0x2A: A = (byte)Rol(A); return;
            case 0x4A: A = (byte)Lsr(A); return;
            case 0x6A: A = (byte)Ror(A); return;
        }

        int aaa = op >> 5, bbb = op >> 2 & 7;
        switch (op & 3)
        {
            case 1: Group1(aaa, bbb); break;
            case 2: Group2(aaa, bbb); break;
            default: Group0(aaa, bbb); break;
        }
    }

    // ORA AND EOR ADC STA LDA CMP SBC
    void Group1(int aaa, int bbb)
    {
        bool store = aaa == 4;
        int address;
        switch (bbb)
        {
            case 0: address = IndirectX(); break;
            case 1: address = ZeroPage(); break;
            case 2: address = PC; PC = (PC + 1) & 0xFFFF; break; // immediate
            case 3: address = Absolute(); break;
            case 4: address = IndirectY(!store); break;
            case 5: address = ZeroPageX(); break;
            case 6: address = AbsoluteIndexed(Y, !store); break;
            default: address = AbsoluteIndexed(X, !store); break;
        }
        if (store) { Wr(address, A); return; }
        int m = Rd(address);
        switch (aaa)
        {
            case 0: A |= (byte)m; SetNZ(A); break;
            case 1: A &= (byte)m; SetNZ(A); break;
            case 2: A ^= (byte)m; SetNZ(A); break;
            case 3: Adc(m); break;
            case 5: A = (byte)m; SetNZ(A); break;
            case 6: Compare(A, m); break;
            default: Sbc(m); break;
        }
    }

    // ASL ROL LSR ROR STX LDX DEC INC
    void Group2(int aaa, int bbb)
    {
        int address;
        bool indexY = aaa == 4 || aaa == 5; // STX and LDX index with Y
        switch (bbb)
        {
            case 0: address = PC; PC = (PC + 1) & 0xFFFF; break; // LDX #
            case 1: address = ZeroPage(); break;
            case 3: address = Absolute(); break;
            case 5: address = indexY ? ZeroPageY() : ZeroPageX(); break;
            default: address = AbsoluteIndexed(indexY ? Y : X, aaa == 5); break; // only LDX takes the page penalty
        }
        switch (aaa)
        {
            case 0: Modify(address, Asl); break;
            case 1: Modify(address, Rol); break;
            case 2: Modify(address, Lsr); break;
            case 3: Modify(address, Ror); break;
            case 4: Wr(address, X); break;
            case 5: X = (byte)Rd(address); SetNZ(X); break;
            case 6: Modify(address, v => { v = (v - 1) & 0xFF; SetNZ(v); return v; }); break;
            default: Modify(address, v => { v = (v + 1) & 0xFF; SetNZ(v); return v; }); break;
        }
    }

    // BIT STY LDY CPY CPX
    void Group0(int aaa, int bbb)
    {
        int address;
        switch (bbb)
        {
            case 0: address = PC; PC = (PC + 1) & 0xFFFF; break;
            case 1: address = ZeroPage(); break;
            case 3: address = Absolute(); break;
            case 5: address = ZeroPageX(); break;
            default: address = AbsoluteIndexed(X, true); break;
        }
        switch (aaa)
        {
            case 1:
                {
                    int m = Rd(address);
                    SetFlag(FlagZ, (A & m) == 0);
                    SetFlag(FlagN, (m & 0x80) != 0);
                    SetFlag(FlagV, (m & 0x40) != 0);
                    break;
                }
            case 4: Wr(address, Y); break;
            case 5: Y = (byte)Rd(address); SetNZ(Y); break;
            case 6: Compare(Y, Rd(address)); break;
            default: Compare(X, Rd(address)); break;
        }
    }

    /// <summary>Base cycle count per opcode; 0 marks an undocumented opcode.</summary>
    static readonly byte[] BaseCycles =
    {
        7,6,0,0,0,3,5,0,3,2,2,0,0,4,6,0,  2,5,0,0,0,4,6,0,2,4,0,0,0,4,7,0,
        6,6,0,0,3,3,5,0,4,2,2,0,4,4,6,0,  2,5,0,0,0,4,6,0,2,4,0,0,0,4,7,0,
        6,6,0,0,0,3,5,0,3,2,2,0,3,4,6,0,  2,5,0,0,0,4,6,0,2,4,0,0,0,4,7,0,
        6,6,0,0,0,3,5,0,4,2,2,0,5,4,6,0,  2,5,0,0,0,4,6,0,2,4,0,0,0,4,7,0,
        0,6,0,0,3,3,3,0,2,0,2,0,4,4,4,0,  2,6,0,0,4,4,4,0,2,5,2,0,0,5,0,0,
        2,6,2,0,3,3,3,0,2,2,2,0,4,4,4,0,  2,5,0,0,4,4,4,0,2,4,2,0,4,4,4,0,
        2,6,0,0,3,3,5,0,2,2,2,0,4,4,6,0,  2,5,0,0,0,4,6,0,2,4,0,0,0,4,7,0,
        2,6,0,0,3,3,5,0,2,2,2,0,4,4,6,0,  2,5,0,0,0,4,6,0,2,4,0,0,0,4,7,0,
    };
}
