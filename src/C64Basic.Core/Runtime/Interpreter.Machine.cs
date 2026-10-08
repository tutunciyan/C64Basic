using System.Diagnostics;
using C64Basic.Core.Machine;
using C64Basic.Core.Parsing;

namespace C64Basic.Core.Runtime;

/// <summary>SYS and USR: running machine code on the 6502 core, with KERNAL and BASIC ROM entry points emulated by traps.</summary>
public sealed partial class Interpreter
{
    // BASIC's floating-point accumulator: exponent at $61, mantissa $62-$65 (leading 1 explicit), sign $66
    const int Fac1 = 0x61, FacSign = 0x66;
    const int UsrVector = 785;

    Cpu6502? _cpu;
    string _chrinLine = "";
    int _chrinPos;
    long _cpuStartTimestamp, _cpuStartCycles;

    /// <summary>The processor machine code runs on; created on first use.</summary>
    Cpu6502 Cpu => _cpu ??= CreateCpu();

    Cpu6502 CreateCpu()
    {
        var cpu = new Cpu6502(_bus) { SystemIrq = SyncJiffies, Tick = CpuTick };

        // screen and keyboard
        cpu.Traps[0xFFD2] = c => { Write(Petscii.ToChar(c.A).ToString()); return TrapResult.Return; };           // CHROUT
        cpu.Traps[0xE544] = c => { Write("\u0093"); return TrapResult.Return; };                                  // clear screen
        cpu.Traps[0xE566] = c => { Write("\u0013"); return TrapResult.Return; };                                  // cursor home
        cpu.Traps[0xFFCF] = Chrin;
        cpu.Traps[0xFFE4] = c =>                                                                                  // GETIN
        {
            string key = TakeKey();
            c.A = key.Length > 0 ? (byte)Petscii.ToCode(key[0]) : (byte)0;
            c.SetNZ(c.A);
            return TrapResult.Return;
        };
        cpu.Traps[0xFFE1] = c =>                                                                                  // STOP
        {
            bool pressed = _dev.BreakRequested;
            _dev.BreakRequested = false;
            c.SetFlag(Cpu6502.FlagZ, pressed);
            return TrapResult.Return;
        };
        cpu.Traps[0xFFF0] = Plot;
        cpu.Traps[0xFFB7] = c => { c.A = (byte)_st; c.SetNZ(c.A); return TrapResult.Return; };                  // READST

        // calls that only matter with real devices attached
        foreach (int nop in new[] { 0xFF9F, 0xFFCC, 0xFFE7 })                                                      // SCNKEY, CLRCHN, CLALL
            cpu.Traps[nop] = c => TrapResult.Return;
        cpu.Traps[0xFFBA] = c => { _bus.Ram[0xB8] = c.A; _bus.Ram[0xBA] = c.X; _bus.Ram[0xB9] = c.Y; return TrapResult.Return; };  // SETLFS
        cpu.Traps[0xFFBD] = c => { _bus.Ram[0xB7] = c.A; _bus.Ram[0xBB] = c.X; _bus.Ram[0xBC] = c.Y; return TrapResult.Return; };  // SETNAM

        // interrupts: the tail of the ROM handler, so user IRQ routines can end with JMP $EA31 / $EA81
        cpu.Traps[0xEA31] = c => { c.AcknowledgeSystemIrq(); c.LeaveInterrupt(); return TrapResult.Continue; };
        cpu.Traps[0xEA7E] = cpu.Traps[0xEA31];
        cpu.Traps[0xEA81] = c => { c.LeaveInterrupt(); return TrapResult.Continue; };

        // leaving machine code for BASIC
        foreach (int ready in new[] { 0xA474, 0xA483, 0xE37B, 0xE394 })
            cpu.Traps[ready] = c => TrapResult.Stop;
        cpu.Traps[0xFCE2] = c => { Reset(); return TrapResult.Stop; };                                            // reset
        cpu.Traps[0xB248] = c => throw new BasicException(ErrorCode.IllegalQuantity);                             // USR default
        return cpu;
    }

    TrapResult Chrin(Cpu6502 c)
    {
        if (_chrinPos >= _chrinLine.Length)
        {
            string? line = _dev.ReadLine();
            if (line == null) { c.A = 0; c.SetFlag(Cpu6502.FlagC, false); return TrapResult.Return; }
            _chrinLine = line + "\r";
            _chrinPos = 0;
        }
        c.A = (byte)Petscii.ToCode(_chrinLine[_chrinPos++]);
        c.SetFlag(Cpu6502.FlagC, false);
        return TrapResult.Return;
    }

    /// <summary>PLOT: carry set reads the cursor into X (row) and Y (column), carry clear moves it there.</summary>
    TrapResult Plot(Cpu6502 c)
    {
        if (c.GetFlag(Cpu6502.FlagC))
        {
            c.X = _bus.Ram[214];
            c.Y = (byte)Col;
            return TrapResult.Return;
        }
        Write("\u0013" + new string('\u0011', c.X) + new string('\u001d', c.Y));
        return TrapResult.Return;
    }

    void Reset()
    {
        _lines.Clear();
        ClearState();
        ProgramChanged();
        Write("\u0093");
        Write(Banner);
        _halted = true;
    }

    /// <summary>Paces the processor to C64 speed (unless running at full speed) and watches for RUN/STOP.</summary>
    bool CpuTick(long cycles)
    {
        if (_dev.BreakRequested) return true;
        if (_opts.StatementsPerSecond > 0)
        {
            double expected = (cycles - _cpuStartCycles) / Cia.ClockHz;
            double actual = Stopwatch.GetElapsedTime(_cpuStartTimestamp).TotalSeconds;
            if (expected - actual > 0.002) Thread.Sleep((int)((expected - actual) * 1000));
        }
        return false;
    }

    CpuStop RunMachine(int address)
    {
        var cpu = Cpu;
        _cpuStartTimestamp = Stopwatch.GetTimestamp();
        _cpuStartCycles = cpu.Cycles;
        SyncJiffies();
        return cpu.Call(address);
    }

    void DoSys(SysStmt s)
    {
        int address = ToInt(Eval(s.Address), 0, 65535);
        var cpu = Cpu;
        // like BASIC's SYS: registers come from $030C-$030F and are stored back afterwards
        cpu.A = _bus.Ram[780]; cpu.X = _bus.Ram[781]; cpu.Y = _bus.Ram[782];
        cpu.P = (byte)((_bus.Ram[783] & ~Cpu6502.FlagB) | Cpu6502.FlagU);

        var stop = RunMachine(address);

        _bus.Ram[780] = cpu.A; _bus.Ram[781] = cpu.X; _bus.Ram[782] = cpu.Y; _bus.Ram[783] = cpu.P;
        switch (stop)
        {
            case CpuStop.IllegalOpcode:
            case CpuStop.NoRom:
                throw new BasicException(ErrorCode.IllegalQuantity); // no undocumented opcodes, no ROM to run
            case CpuStop.Break:
            case CpuStop.Terminated when !_halted:
                // BRK or a jump to BASIC's warm start: the program ends at READY.
                _halted = true;
                _canCont = false;
                break;
        }
    }

    double CallUsr(double argument)
    {
        WriteFac1(argument);
        var stop = RunMachine(UsrVector);
        if (stop == CpuStop.IllegalOpcode || stop == CpuStop.NoRom) throw new BasicException(ErrorCode.IllegalQuantity);
        return ReadFac1();
    }

    // ---------- floating-point accumulator ----------
    void WriteFac1(double value)
    {
        var ram = _bus.Ram;
        Array.Clear(ram, Fac1, 6);
        if (value == 0 || double.IsNaN(value)) return;

        double m = Math.Abs(value);
        int exponent = (int)Math.Floor(Math.Log2(m)) + 1;
        double fraction = m / Math.Pow(2, exponent);          // in [0.5, 1)
        if (fraction >= 1) { fraction /= 2; exponent++; }
        if (fraction < 0.5) { fraction *= 2; exponent--; }
        long mantissa = (long)Math.Round(fraction * 4294967296.0);
        if (mantissa > 0xFFFFFFFFL) { mantissa = 0x80000000L; exponent++; }

        int biased = exponent + 128;
        if (biased > 255) throw new BasicException(ErrorCode.Overflow);
        if (biased < 1) return;                                // underflows to zero
        ram[Fac1] = (byte)biased;
        ram[Fac1 + 1] = (byte)(mantissa >> 24);
        ram[Fac1 + 2] = (byte)(mantissa >> 16);
        ram[Fac1 + 3] = (byte)(mantissa >> 8);
        ram[Fac1 + 4] = (byte)mantissa;
        ram[FacSign] = value < 0 ? (byte)0xFF : (byte)0;
    }

    double ReadFac1()
    {
        var ram = _bus.Ram;
        int exponent = ram[Fac1];
        if (exponent == 0) return 0;
        double mantissa = ((long)ram[Fac1 + 1] << 24 | (long)ram[Fac1 + 2] << 16 | (long)ram[Fac1 + 3] << 8 | ram[Fac1 + 4]) / 4294967296.0;
        double value = mantissa * Math.Pow(2, exponent - 128);
        return (ram[FacSign] & 0x80) != 0 ? -value : value;
    }
}
