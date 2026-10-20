using LocalSeek.Core.Index;
using LocalSeek.Tests.Fakes;
using static LocalSeek.Tests.Index.IndexWriterTests;

namespace LocalSeek.Tests.Index;

/// <summary>
/// Acceptance #15: a fault is injected at every I/O operation of the operation under test, the
/// process "crashes" (unflushed data lost, optionally torn) and the index is reopened. The result must
/// be exactly the state before or after the operation, with no manual repair and no reported issues.
/// </summary>
public class FaultInjectionTests
{
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly IndexWriterOptions SmallBuffer = new() { RamBufferBytes = 1500 };

    private sealed record Scenario(string Name, Action<IndexWriter> Setup, Action<IndexWriter> Operation);

    private static void Base(IndexWriter writer)
    {
        for (var i = 0; i < 6; i++)
        {
            writer.AddOrUpdate(Doc($@"C:\base\{i}.txt", $"apple 台灣大學 base{i}", hash: (ulong)i));
        }

        writer.Commit();
        writer.AddOrUpdate(Doc(@"C:\base\late.txt", "late banana"));
        writer.Commit();
    }

    private static readonly Scenario[] Scenarios =
    [
        new("adds", Base, w =>
        {
            w.AddOrUpdate(Doc(@"C:\new\1.txt", "cherry 資料"));
            w.AddOrUpdate(Doc(@"C:\new\2.txt", "durian"));
            w.Commit();
        }),
        new("deletes", Base, w =>
        {
            w.Delete(@"C:\base\1.txt");
            w.Delete(@"C:\base\late.txt");
            w.Commit();
        }),
        new("renames", Base, w =>
        {
            w.Rename(@"C:\base\2.txt", @"C:\moved\二.txt", 10, T0);
            w.Rename(@"C:\base\late.txt", @"C:\moved\late.txt", 10, T0);
            w.Commit();
        }),
        new("mixed", Base, w =>
        {
            w.AddOrUpdate(Doc(@"C:\base\3.txt", "updated content"));
            w.Delete(@"C:\base\4.txt");
            w.Rename(@"C:\base\5.txt", @"C:\moved\5.txt", 10, T0);
            w.AddOrUpdate(Doc(@"C:\new\x.txt", "x"));
            w.Commit();
        }),
        // The emptied segment's file is released by the commit under test, so cleanup I/O is covered too.
        new(
            "drop-segment",
            w =>
            {
                Base(w);
                w.Delete(@"C:\base\late.txt");
                w.Commit();
            },
            w =>
            {
                w.AddOrUpdate(Doc(@"C:\new\after.txt", "after"));
                w.Commit();
            }),
        // Merging rewrites segments (incl. renamed docs whose content lives elsewhere) without changing content.
        new(
            "merge",
            w =>
            {
                Base(w);
                w.Rename(@"C:\base\2.txt", @"C:\moved\2.txt", 10, T0);
                w.Delete(@"C:\base\3.txt");
                w.AddOrUpdate(Doc(@"C:\base\third.txt", "third segment"));
                w.Commit();
            },
            w => Assert.True(w.ForceMerge())),
        new("auto-flush", Base, w =>
        {
            for (var i = 0; i < 25; i++)
            {
                w.AddOrUpdate(Doc($@"C:\bulk\{i}.txt", $"bulk 資料 item{i} apple"));
            }

            w.Commit();
        }),
    ];

    public static TheoryData<string, bool> Cases()
    {
        var data = new TheoryData<string, bool>();
        foreach (var s in Scenarios)
        {
            data.Add(s.Name, false);
            data.Add(s.Name, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Crash_at_any_io_operation_leaves_before_or_after_state(string scenarioName, bool tornWrites)
    {
        var scenario = Scenarios.Single(s => s.Name == scenarioName);
        var (before, after, setupOps, totalOps) = Reference(scenario);
        Assert.True(totalOps - setupOps >= 5, "Operation under test should perform several I/O operations.");

        var outcomes = new HashSet<string>();
        for (var k = setupOps + 1; k <= totalOps; k++)
        {
            var storage = new FakeIndexStorage();
            RunWithFault(storage, scenario, k);
            storage.SimulateCrash(tornWrites ? new Random(k) : null);

            using var reopened = IndexWriter.Open(storage, SmallBuffer);
            var dump = LogicalDump.Of(reopened);
            Assert.True(dump == before || dump == after, $"Fault at operation {k} ({storage.Log[k - 1]}) produced a third state.");
            Assert.False(reopened.OpenReport.HasIssues, $"Fault at operation {k} required repair: {string.Join("; ", reopened.OpenReport.Issues)}");
            outcomes.Add(dump == before ? "before" : "after");

            reopened.AddOrUpdate(Doc(@"C:\probe.txt", "probe"));
            reopened.Commit();
        }

        if (before != after)
        {
            Assert.Contains("before", outcomes);
            Assert.Contains("after", outcomes);
        }
    }

    [Fact]
    public void Merge_scenario_really_rewrites_segments()
    {
        var storage = new FakeIndexStorage();
        using var writer = IndexWriter.Open(storage, SmallBuffer);
        var scenario = Scenarios.Single(s => s.Name == "merge");
        scenario.Setup(writer);
        var segmentsBefore = writer.GetStatistics().SegmentCount;

        scenario.Operation(writer);

        Assert.True(segmentsBefore >= 3);
        Assert.Equal(1, writer.GetStatistics().SegmentCount);
    }

    [Fact]
    public void Recovery_after_a_crash_is_itself_crash_safe()
    {
        var scenario = Scenarios.Single(s => s.Name == "mixed");
        var (before, after, setupOps, totalOps) = Reference(scenario);

        for (var k = setupOps + 1; k <= totalOps; k += 3)
        {
            // Count the I/O operations recovery performs for this crash.
            var probe = new FakeIndexStorage();
            RunWithFault(probe, scenario, k);
            probe.SimulateCrash(new Random(k));
            var recoveryStart = probe.OperationCount;
            IndexWriter.Open(probe, SmallBuffer).Dispose();
            var recoveryOps = probe.OperationCount - recoveryStart;

            for (var j = 1; j <= recoveryOps; j++)
            {
                var storage = new FakeIndexStorage();
                RunWithFault(storage, scenario, k);
                storage.SimulateCrash(new Random(k));
                storage.FailAtOperation = storage.OperationCount + j;
                try
                {
                    IndexWriter.Open(storage, SmallBuffer).Dispose();
                }
                catch (IOException)
                {
                    // Crash during recovery — the case under test.
                }

                storage.SimulateCrash(new Random(j));
                using var reopened = IndexWriter.Open(storage, SmallBuffer);
                var dump = LogicalDump.Of(reopened);
                Assert.True(dump == before || dump == after, $"Crash at {k}, then during recovery at {j}, produced a third state.");
            }
        }
    }

    private static (string Before, string After, int SetupOps, int TotalOps) Reference(Scenario scenario)
    {
        var storage = new FakeIndexStorage();
        using var writer = IndexWriter.Open(storage, SmallBuffer);
        scenario.Setup(writer);
        var before = LogicalDump.Of(writer);
        var setupOps = storage.OperationCount;
        scenario.Operation(writer);
        return (before, LogicalDump.Of(writer), setupOps, storage.OperationCount);
    }

    private static void RunWithFault(FakeIndexStorage storage, Scenario scenario, int failAt)
    {
        var writer = IndexWriter.Open(storage, SmallBuffer);
        try
        {
            scenario.Setup(writer);
            storage.FailAtOperation = failAt;
            scenario.Operation(writer);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // Expected: the injected fault, or the writer refusing work after it.
        }
        finally
        {
            writer.Dispose();
        }

        Assert.True(storage.OperationCount >= failAt || storage.Crashed, "The fault point was never reached.");
    }
}
