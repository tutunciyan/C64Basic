using C64Basic.Core.IO;
using C64Basic.Core.Machine;

namespace C64Basic.Tests;

public class JoystickTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(5000, -5000, 0)]                                   // inside the dead zone
    [InlineData(-30000, 0, JoystickMapping.Left)]
    [InlineData(30000, 0, JoystickMapping.Right)]
    [InlineData(0, -30000, JoystickMapping.Up)]
    [InlineData(0, 30000, JoystickMapping.Down)]
    [InlineData(-20000, -20000, JoystickMapping.Left | JoystickMapping.Up)]
    [InlineData(20000, 20000, JoystickMapping.Right | JoystickMapping.Down)]
    public void AxesBecomeDirections(int x, int y, int expected) =>
        Assert.Equal(expected, JoystickMapping.FromAxes(x, y));

    [Fact]
    public void TheDeadZoneCanBeChanged()
    {
        Assert.Equal(0, JoystickMapping.FromAxes(9000, 0));
        Assert.Equal(JoystickMapping.Right, JoystickMapping.FromAxes(9000, 0, deadZone: 5000));
    }

    [Fact]
    public void OppositeDirectionsCancel()
    {
        Assert.Equal(JoystickMapping.Fire, JoystickMapping.Normalize(JoystickMapping.Up | JoystickMapping.Down | JoystickMapping.Fire));
        Assert.Equal(JoystickMapping.Up, JoystickMapping.Normalize(JoystickMapping.Up | JoystickMapping.Left | JoystickMapping.Right));
        Assert.Equal(JoystickMapping.Up | JoystickMapping.Left, JoystickMapping.Normalize(JoystickMapping.Up | JoystickMapping.Left));
    }

    [Fact]
    public void SourcesOnTheSamePortAreCombined()
    {
        var console = new ScreenConsole();
        console.SetJoystick(2, JoystickMapping.Left, source: 0);               // keyboard
        console.SetJoystick(2, JoystickMapping.Fire, source: 1);               // a pad
        Assert.Equal(JoystickMapping.Left | JoystickMapping.Fire, console.Joystick(2));
        Assert.Equal(0, console.Joystick(1));
    }

    [Fact]
    public void ReleasingOneSourceLeavesTheOther()
    {
        var console = new ScreenConsole();
        console.SetJoystick(2, JoystickMapping.Up, 0);
        console.SetJoystick(2, JoystickMapping.Up | JoystickMapping.Fire, 1);
        console.SetJoystick(2, 0, 1);
        Assert.Equal(JoystickMapping.Up, console.Joystick(2));
    }

    [Fact]
    public void OpposingSourcesCancelEachOther()
    {
        var console = new ScreenConsole();
        console.SetJoystick(2, JoystickMapping.Left, 0);
        console.SetJoystick(2, JoystickMapping.Right, 1);
        Assert.Equal(0, console.Joystick(2));
    }

    [Fact]
    public void BothPortsAreIndependent()
    {
        var console = new ScreenConsole();
        console.SetJoystick(1, JoystickMapping.Fire, 1);
        console.SetJoystick(2, JoystickMapping.Up, 2);
        Assert.Equal(JoystickMapping.Fire, console.Joystick(1));
        Assert.Equal(JoystickMapping.Up, console.Joystick(2));
    }

    [Fact]
    public void InvalidPortsAndSourcesAreIgnored()
    {
        var console = new ScreenConsole();
        console.SetJoystick(0, 1);
        console.SetJoystick(3, 1);
        console.SetJoystick(2, 1, source: 99);
        console.SetJoystick(2, 1, source: -1);
        Assert.Equal(0, console.Joystick(0));
        Assert.Equal(0, console.Joystick(2));
    }

    [Fact]
    public void ReleaseAllClearsEverySource()
    {
        var console = new ScreenConsole();
        console.SetJoystick(1, 1, 3);
        console.SetJoystick(2, 2, 0);
        console.ReleaseAllKeys();
        Assert.Equal(0, console.Joystick(1));
        Assert.Equal(0, console.Joystick(2));
    }

    [Fact]
    public void Port1ReadsThroughTheCiaOnPortB()
    {
        var console = new ScreenConsole();
        var bus = new Bus();
        console.Attach(bus);
        console.SetJoystick(1, JoystickMapping.Fire | JoystickMapping.Right, 1);
        bus.Write(0xDC03, 0);                                          // port B as input
        Assert.Equal(0b1110_0111, bus.Read(0xDC01));                   // right (bit 3) and fire (bit 4) pulled low
        console.SetJoystick(2, JoystickMapping.Up, 0);
        bus.Write(0xDC02, 0);
        Assert.Equal(0b1111_1110, bus.Read(0xDC00));                   // joystick 2 on port A, bit 0 = up
    }
}
