namespace C64Basic.Core.Machine;

/// <summary>A processor (or anything that runs in whole steps) that <see cref="Lockstep"/> keeps in time with the others.</summary>
public interface ILockstepMember
{
    /// <summary>Cycles on the member's own clock since power-on.</summary>
    long Cycles { get; }

    /// <summary>The member's clock in Hz (985248 for the C64, 1000000 for the 1541).</summary>
    double Hz { get; }

    /// <summary>Runs one instruction (or interrupt entry); <see cref="Cycles"/> grows by its length.</summary>
    void Step();
}

/// <summary>
/// Runs several processors with different clocks side by side: the one that is furthest behind in time always takes the next
/// step, so no member is ever more than one instruction ahead of another, and what one writes is seen by the others at about the
/// right moment. This is what lets the C64 and the 1541 talk over the serial bus with timing a fast loader can rely on.
/// </summary>
public sealed class Lockstep
{
    readonly ILockstepMember[] _members;

    public Lockstep(params ILockstepMember[] members)
    {
        if (members.Length == 0) throw new ArgumentException("nothing to run", nameof(members));
        _members = members;
    }

    public IReadOnlyList<ILockstepMember> Members => _members;

    /// <summary>The time, in seconds, that a member has reached.</summary>
    public static double TimeOf(ILockstepMember m) => m.Cycles / m.Hz;

    /// <summary>The member that is furthest behind (the first one on a tie, so the order of the members is the tie-break).</summary>
    public ILockstepMember Next()
    {
        var next = _members[0];
        double best = TimeOf(next);
        for (int i = 1; i < _members.Length; i++)
        {
            double t = TimeOf(_members[i]);
            if (t < best) { best = t; next = _members[i]; }
        }
        return next;
    }

    /// <summary>Steps members until every one has reached <paramref name="seconds"/>, or <paramref name="halt"/> returns true.</summary>
    public void RunUntil(double seconds, Func<bool>? halt = null)
    {
        while (true)
        {
            var next = Next();
            if (TimeOf(next) >= seconds) return;
            next.Step();
            if (halt?.Invoke() == true) return;
        }
    }

    /// <summary>Steps the member that is furthest behind, <paramref name="steps"/> times.</summary>
    public void Run(int steps)
    {
        for (int i = 0; i < steps; i++) Next().Step();
    }
}

/// <summary>A native <see cref="Cpu6502"/> as a lockstep member.</summary>
public sealed class CpuMember : ILockstepMember
{
    public Cpu6502 Cpu { get; }
    public double Hz { get; }

    /// <summary>Called after every step (cycle stealing, the disk mechanics, ...); may add to the CPU's cycles.</summary>
    public Action<Cpu6502>? AfterStep { get; set; }

    public CpuMember(Cpu6502 cpu, double hz) { Cpu = cpu; Hz = hz; }

    public long Cycles => Cpu.Cycles;

    public void Step()
    {
        Cpu.StepNative();
        AfterStep?.Invoke(Cpu);
    }
}
