namespace C64Basic.Core.Machine;

/// <summary>A processor (or anything that runs in whole steps) that <see cref="Lockstep"/> keeps in time with the others.</summary>
public interface ILockstepMember
{
    /// <summary>Cycles on the member's own clock since power-on.</summary>
    long Cycles { get; }

    /// <summary>The member's clock in Hz (985248 for the C64, 1000000 for the 1541).</summary>
    double Hz { get; }

    /// <summary>
    /// The time (in seconds) of the first thing the next step does that another member could notice (for a processor, the data
    /// access in the last cycle of the next instruction). Members are stepped in the order of this time, which is what makes a
    /// store by one processor and a load by the other happen in the right order within the length of an instruction.
    /// </summary>
    double NextAccessTime => Cycles / Hz;

    /// <summary>Runs one instruction (or interrupt entry); <see cref="Cycles"/> grows by its length.</summary>
    void Step();
}

/// <summary>
/// Runs several processors with different clocks side by side: the one whose next bus access comes first always takes the next
/// step, so no member is ever more than one instruction ahead of another, and what one writes is seen by the others at the
/// right cycle. This is what lets the C64 and the 1541 talk over the serial bus with timing a fast loader can rely on.
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

    /// <summary>The member whose next observable access comes first (the first one on a tie, so the order of the members is the tie-break).</summary>
    public ILockstepMember Next()
    {
        var next = _members[0];
        double best = next.NextAccessTime;
        for (int i = 1; i < _members.Length; i++)
        {
            double t = _members[i].NextAccessTime;
            if (t < best) { best = t; next = _members[i]; }
        }
        return next;
    }

    /// <summary>
    /// Steps members until the next access of every one is at or beyond <paramref name="seconds"/> (so each has reached it, give or
    /// take an instruction), or <paramref name="halt"/> returns true. Returns the number of steps taken.
    /// </summary>
    public int RunUntil(double seconds, Func<bool>? halt = null)
    {
        int steps = 0;
        while (true)
        {
            var next = Next();
            if (next.NextAccessTime >= seconds) return steps;
            next.Step();
            steps++;
            if (halt?.Invoke() == true) return steps;
        }
    }

    /// <summary>Takes <paramref name="steps"/> steps, each by the member whose next access comes first.</summary>
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

    public double NextAccessTime => Cpu.NextAccessCycle / Hz;

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
