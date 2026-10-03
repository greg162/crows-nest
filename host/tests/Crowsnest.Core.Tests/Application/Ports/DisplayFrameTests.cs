using Crowsnest.Core.Application;
using Crowsnest.Core.Application.Diagnostics;
using Crowsnest.Core.Application.Ports;

namespace Crowsnest.Core.Tests.Application.Ports;

public class DisplayFrameTests
{
    [Fact]
    public void CursorSpanIsAHalfOpenRangeIntoTheFormattedText()
    {
        // The spec's worked example: 4..7 underlines the kHz digits of "121.500" (§5.2).
        // The firmware trusts this blindly, so the host has to be right about it.
        FieldDescriptor standby = SelfTestFrames.ComExample().Fields[0];

        Assert.Equal("121.500", standby.Text);
        Assert.Equal("500", standby.Text[standby.CursorSpan!.Value]);
    }

    [Theory]
    [MemberData(nameof(SelfTestFields))]
    public void EverySelfTestCursorSpanFallsInsideItsText(string text, Range? span)
    {
        if (span is null)
        {
            return;
        }

        (int offset, int length) = span.Value.GetOffsetAndLength(text.Length);

        Assert.InRange(offset, 0, text.Length);
        Assert.InRange(offset + length, offset, text.Length);
        Assert.True(length > 0, "A cursor that underlines nothing is a bug, not a cursor.");
    }

    [Fact]
    public void SelfTestFramesCoverEveryLayoutTheFirmwareImplements()
    {
        // A board port is correct when it renders all three. If a layout is added to
        // PageLayout without a self-test frame, bring-up stops covering it.
        IEnumerable<PageLayout> covered = SelfTestFrames.All().Select(f => f.Page.Layout);

        Assert.Equal(Enum.GetValues<PageLayout>().Order(), covered.Order());
    }

    [Fact]
    public void SelfTestFrameRevisionsIncreaseSoTheDeviceAppliesThemAll()
    {
        // The device discards any frame whose revision is not newer than the last applied
        // (spec §6.1), so a sweep with a flat revision would silently render once.
        IReadOnlyList<long> revisions = [.. SelfTestFrames.All().Select(f => f.Revision)];

        Assert.Equal(revisions.Order(), revisions);
        Assert.Equal(revisions.Distinct().Count(), revisions.Count);
    }

    [Fact]
    public void NoSelfTestFrameExceedsTheReferencePanelFieldCount()
    {
        // The reference CrowPanel reports maxFields: 3 (spec §6.1).
        Assert.All(SelfTestFrames.All(), frame => Assert.InRange(frame.Fields.Count, 1, 3));
    }

    public static TheoryData<string, Range?> SelfTestFields()
    {
        TheoryData<string, Range?> data = [];
        foreach (DisplayFrame frame in SelfTestFrames.All())
        {
            foreach (FieldDescriptor field in frame.Fields)
            {
                data.Add(field.Text, field.CursorSpan);
            }
        }

        return data;
    }
}
