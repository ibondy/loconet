using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocoNet.Net.Tests;

[TestClass]
public class JmriLineParserTests
{
    [TestMethod]
    public void ParsesSendLine()
    {
        // OPC_GPON = 0x83, checksum 0x7C.
        Assert.True(JmriLineParser.TryParse("SEND 83 7C", out var kind, out var msg));
        Assert.Equal(JmriLineParser.LineKind.Send, kind);
        Assert.Equal(OpCode.GpOn, msg.OpCode);
    }

    [TestMethod]
    public void ParsesReceiveLine_CaseInsensitive_WithExtraWhitespace()
    {
        Assert.True(JmriLineParser.TryParse("  receive   83   7C  ", out var kind, out var msg));
        Assert.Equal(JmriLineParser.LineKind.Receive, kind);
        Assert.Equal(OpCode.GpOn, msg.OpCode);
    }

    [TestMethod]
    public void IgnoresComments()
    {
        Assert.True(JmriLineParser.TryParse("SEND 83 7C  # turn power on", out var kind, out var msg));
        Assert.Equal(JmriLineParser.LineKind.Send, kind);
        Assert.Equal(OpCode.GpOn, msg.OpCode);
    }

    [TestMethod]
    public void RejectsBadKeyword()
    {
        Assert.False(JmriLineParser.TryParse("HELLO 83 7C", out _, out _));
    }

    [TestMethod]
    public void RejectsBadChecksum()
    {
        // Wrong checksum byte → LnMsg.FromBytes throws → parser returns false.
        Assert.False(JmriLineParser.TryParse("SEND 83 00", out _, out _));
    }

    [TestMethod]
    public void RejectsOddHex()
    {
        Assert.False(JmriLineParser.TryParse("SEND 8 7C", out _, out _));
    }

    [TestMethod]
    public void EmptyOrCommentLineReturnsFalseWithNoneKind()
    {
        Assert.False(JmriLineParser.TryParse("", out var k1, out _));
        Assert.Equal(JmriLineParser.LineKind.None, k1);

        Assert.False(JmriLineParser.TryParse("   # just a comment", out var k2, out _));
        Assert.Equal(JmriLineParser.LineKind.None, k2);
    }

    [TestMethod]
    public void FormatSend_RoundTripsThroughParser()
    {
        var msg = LnMsg.Make(OpCode.LocoSpd, 0x05, 0x40);
        string line = JmriLineParser.FormatSend(msg);
        Assert.StartsWith("SEND ", line);

        Assert.True(JmriLineParser.TryParse(line, out var kind, out var parsed));
        Assert.Equal(JmriLineParser.LineKind.Send, kind);
        Assert.Equal(msg, parsed);
    }

    [TestMethod]
    public void FormatReceive_RoundTripsThroughParser()
    {
        var msg = LnMsg.MakeLongAck((byte)OpCode.PeerXfer, 0x7F);
        string line = JmriLineParser.FormatReceive(msg);
        Assert.StartsWith("RECEIVE ", line);

        Assert.True(JmriLineParser.TryParse(line, out var kind, out var parsed));
        Assert.Equal(JmriLineParser.LineKind.Receive, kind);
        Assert.Equal(msg, parsed);
    }
}
