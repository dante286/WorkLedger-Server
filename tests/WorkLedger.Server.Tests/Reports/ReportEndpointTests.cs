using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using WorkLedger.Contracts.Reports;
using WorkLedger.Server.Data.Entities;
using WorkLedger.Server.Tests.Auth;

namespace WorkLedger.Server.Tests.Reports;

/// <summary>
/// Reports over a real Postgres, through the real HTTP surface.
///
/// <para>Unlike the sync tests, these do <b>not</b> share one user. A weekly report is a user-wide
/// total, so any other test's time entries landing under the same user would show up in the number
/// being asserted. Each test therefore provisions its own user and device through the fixture, which
/// mints a token signed with the same key the host validates against. That the endpoints accept such a
/// token at all is the point of the isolation test below: user scope comes from the claim, and from
/// nothing else.</para>
///
/// <para>The seed week is deliberately in 2031, far from the 2026 timestamps the sync tests use, so a
/// mistake in the user filter shows up as a wrong total here rather than passing by luck.</para>
/// </summary>
[Collection(nameof(AuthCollection))]
public class ReportEndpointTests(AuthWebAppFixture fixture)
{
    /// <summary>Monday of the ISO week every seeded fixture below is built around.</summary>
    private static readonly DateOnly WeekStart = new(2031, 3, 3);

    [Fact]
    public async Task Weekly_WithoutToken_Returns401()
    {
        var response = await fixture.Factory.CreateClient().GetAsync("/reports/weekly");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Projects_WithoutToken_Returns401()
    {
        var response = await fixture.Factory.CreateClient().GetAsync($"/reports/projects/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Tasks_WithoutToken_Returns401()
    {
        var response = await fixture.Factory.CreateClient().GetAsync($"/reports/tasks/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The whole seed in one assertion set, against hand-computed numbers — see <see cref="SeedWeekAsync"/>
    /// for what goes in. User total, per-project, per-task and per-day all come from one aggregation,
    /// so checking them together is what proves they agree.
    /// </summary>
    [Fact]
    public async Task Weekly_TotalsMatchTheSeededWeek()
    {
        var user = await NewUserAsync();
        var seed = await SeedWeekAsync(user);

        var report = await GetAsync<WeeklyReportDto>(user, $"/reports/weekly?weekOf={WeekStart:yyyy-MM-dd}");

        Assert.Equal(WeekStart, report.WeekStart);
        Assert.Equal(WeekStart.AddDays(6), report.WeekEnd);

        // 5400 (Mon, Design) + 7200 (Tue, Build) + 5400 (Thu, Support) + 900 (Sun, Support).
        Assert.Equal(18900, report.TotalSeconds);

        Assert.Equal(
            [("Alpha", 12600L), ("Beta", 6300L)],
            report.Projects.Select(p => (p.ProjectName, p.Seconds)));

        Assert.Equal(
            [("Build", 7200L), ("Design", 5400L), ("Support", 6300L)],
            report.Tasks.Select(t => (t.TaskName, t.Seconds)));

        // Every task names its project, so a bare "Design" is never ambiguous.
        Assert.All(report.Tasks, t => Assert.Equal(t.TaskName == "Support" ? "Beta" : "Alpha", t.ProjectName));
        Assert.Contains(report.Tasks, t => t.ProjectSyncId == seed.Alpha && t.TaskSyncId == seed.Design);

        // Seven days, zero-filled, Monday first.
        Assert.Equal(
            [5400L, 7200L, 0L, 5400L, 0L, 0L, 900L],
            report.Days.Select(d => d.Seconds));
        Assert.Equal(
            Enumerable.Range(0, 7).Select(WeekStart.AddDays),
            report.Days.Select(d => d.Date));

        // The Friday timer is still going: excluded from every total, but not hidden.
        Assert.Equal(1, report.RunningEntries);
    }

    [Fact]
    public async Task Weekly_MergesTwoEntriesOnTheSameDayAndTask()
    {
        var user = await NewUserAsync();
        await SeedWeekAsync(user);

        var report = await GetAsync<WeeklyReportDto>(user, $"/reports/weekly?weekOf={WeekStart:yyyy-MM-dd}");

        // Monday holds two separate Design entries (3600 + 1800) and must report one 5400 bucket.
        Assert.Equal(5400, report.Days[0].Seconds);
        Assert.Equal(5400, Assert.Single(report.Tasks, t => t.TaskName == "Design").Seconds);
    }

    /// <summary>
    /// Any day inside the week resolves to the same ISO week — the parameter names a week, not a start
    /// date, so a caller passing "today" gets this week rather than a seven-day window from today.
    /// </summary>
    [Fact]
    public async Task Weekly_AnyDayInTheWeek_ResolvesToTheSameMondayThroughSunday()
    {
        var user = await NewUserAsync();
        await SeedWeekAsync(user);

        var fromThursday = await GetAsync<WeeklyReportDto>(user, "/reports/weekly?weekOf=2031-03-06");
        var fromSunday = await GetAsync<WeeklyReportDto>(user, "/reports/weekly?weekOf=2031-03-09");

        Assert.Equal(WeekStart, fromThursday.WeekStart);
        Assert.Equal(WeekStart, fromSunday.WeekStart);
        Assert.Equal(18900, fromSunday.TotalSeconds);
    }

    [Fact]
    public async Task Weekly_WithNoWeekOf_ReportsTheCurrentUtcWeek()
    {
        var user = await NewUserAsync();

        var report = await GetAsync<WeeklyReportDto>(user, "/reports/weekly");

        var expected = DateOnly.FromDateTime(DateTime.UtcNow);
        Assert.Equal(expected.AddDays(-(((int)expected.DayOfWeek + 6) % 7)), report.WeekStart);
    }

    /// <summary>
    /// The seed puts an entry at 23:59 the day before the week and another at 00:00 the day after, so
    /// an off-by-one at either edge lands in the total rather than passing silently.
    /// </summary>
    [Fact]
    public async Task Weekly_ExcludesEntriesJustOutsideTheWeek()
    {
        var user = await NewUserAsync();
        await SeedWeekAsync(user);

        var previous = await GetAsync<WeeklyReportDto>(user, "/reports/weekly?weekOf=2031-02-24");
        var next = await GetAsync<WeeklyReportDto>(user, "/reports/weekly?weekOf=2031-03-10");

        // Each neighbouring week holds exactly the one entry the seeded week must not have counted.
        Assert.Equal(1000, previous.TotalSeconds);
        Assert.Equal(2000, next.TotalSeconds);
    }

    [Fact]
    public async Task Weekly_ExcludesTombstonedEntriesTasksAndProjects()
    {
        var user = await NewUserAsync();
        await SeedWeekAsync(user);

        var report = await GetAsync<WeeklyReportDto>(user, $"/reports/weekly?weekOf={WeekStart:yyyy-MM-dd}");

        // The seed hides 9999s behind a tombstoned entry, 4444s behind a tombstoned task, and 3333s
        // behind a tombstoned project — all on days that are otherwise accounted for exactly.
        Assert.Equal(18900, report.TotalSeconds);
        Assert.DoesNotContain(report.Tasks, t => t.TaskName == "Deleted task");
        Assert.DoesNotContain(report.Projects, p => p.ProjectName == "Deleted project");
        Assert.Equal(0, report.Days[2].Seconds);
    }

    /// <summary>
    /// The client writes DurationSeconds when a timer stops, but a row that arrived without one still
    /// represents real time — it must fall back to EndTime − StartTime rather than count as zero.
    /// </summary>
    [Fact]
    public async Task Weekly_FallsBackToEndMinusStartWhenDurationSecondsIsMissing()
    {
        var user = await NewUserAsync();
        var project = Guid.NewGuid();
        var task = Guid.NewGuid();

        await SaveAsync(
            NewProject(user.Id, project, "Alpha"),
            NewTask(user.Id, task, project, "Design"),
            NewEntry(user.Id, task, At(2031, 3, 3, 9), end: At(2031, 3, 3, 11), durationSeconds: null));

        var report = await GetAsync<WeeklyReportDto>(user, $"/reports/weekly?weekOf={WeekStart:yyyy-MM-dd}");

        Assert.Equal(7200, report.TotalSeconds);
    }

    [Fact]
    public async Task Weekly_Csv_HasTheDocumentedColumnsAndOneRowPerDayProjectTask()
    {
        var user = await NewUserAsync();
        await SeedWeekAsync(user);

        var response = await user.Client.GetAsync($"/reports/weekly?weekOf={WeekStart:yyyy-MM-dd}&format=csv");
        response.EnsureSuccessStatusCode();

        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);

        Assert.Equal(
            [
                "date,project,task,seconds",
                "2031-03-03,Alpha,Design,5400",
                "2031-03-04,Alpha,Build,7200",
                "2031-03-06,Beta,Support,5400",
                "2031-03-09,Beta,Support,900",
            ],
            await CsvLinesAsync(response));
    }

    [Fact]
    public async Task Weekly_Csv_ViaAcceptHeader_IsTheSameAsFormatCsv()
    {
        var user = await NewUserAsync();
        await SeedWeekAsync(user);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/reports/weekly?weekOf={WeekStart:yyyy-MM-dd}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/csv"));

        var response = await user.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("date,project,task,seconds", (await CsvLinesAsync(response))[0]);
    }

    /// <summary>
    /// A wildcard Accept is what most HTTP clients send by default; answering it with a file download
    /// would surprise every caller that never asked for one.
    /// </summary>
    [Fact]
    public async Task Weekly_WithWildcardAccept_StaysJson()
    {
        var user = await NewUserAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/reports/weekly");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

        var response = await user.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>
    /// Names are free text. An unescaped comma would shift every column after it and the file would
    /// still parse, so the damage would be silent.
    /// </summary>
    [Fact]
    public async Task Csv_QuotesNamesContainingCommasAndQuotes()
    {
        var user = await NewUserAsync();
        var project = Guid.NewGuid();
        var task = Guid.NewGuid();

        await SaveAsync(
            NewProject(user.Id, project, "Alpha, Inc."),
            NewTask(user.Id, task, project, "Say \"hello\""),
            NewEntry(user.Id, task, At(2031, 3, 3, 9), At(2031, 3, 3, 10), 3600));

        var response = await user.Client.GetAsync($"/reports/weekly?weekOf={WeekStart:yyyy-MM-dd}&format=csv");

        Assert.Equal(
            "2031-03-03,\"Alpha, Inc.\",\"Say \"\"hello\"\"\",3600",
            (await CsvLinesAsync(response))[1]);
    }

    [Fact]
    public async Task Weekly_UnknownFormat_Returns400()
    {
        var user = await NewUserAsync();

        var response = await user.Client.GetAsync("/reports/weekly?format=xlsx");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("last-tuesday")]
    [InlineData("2031-13-01")]
    // The ISO-week expansion adds six days to whatever is sent, which runs off the end of DateOnly here
    // and would throw — a 400 with a reason, not a 500.
    [InlineData("9999-12-31")]
    public async Task Weekly_UnusableWeekOf_Returns400(string weekOf)
    {
        var user = await NewUserAsync();

        var response = await user.Client.GetAsync($"/reports/weekly?weekOf={weekOf}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>The inclusive range end adds a day to `to`, with the same overflow to avoid.</summary>
    [Fact]
    public async Task Project_ToAtTheEndOfDateOnlysRange_Returns400()
    {
        var user = await NewUserAsync();
        var seed = await SeedWeekAsync(user);

        var response = await user.Client.GetAsync($"/reports/projects/{seed.Alpha}?to=9999-12-31");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Project_ReportsItsOwnTasksOnly()
    {
        var user = await NewUserAsync();
        var seed = await SeedWeekAsync(user);

        var report = await GetAsync<ProjectReportDto>(user, $"/reports/projects/{seed.Alpha}");

        Assert.Equal("Alpha", report.ProjectName);
        Assert.Null(report.From);
        Assert.Null(report.To);

        // All-time, so the entries either side of the seeded week count here: 12600 + 1000 + 2000.
        Assert.Equal(15600, report.TotalSeconds);
        Assert.Equal(
            [("Build", 9200L), ("Design", 6400L)],
            report.Tasks.Select(t => (t.TaskName, t.Seconds)));
        Assert.DoesNotContain(report.Tasks, t => t.TaskName == "Support");
    }

    /// <summary>
    /// Both ends are inclusive whole days, so an entry at 23:59 on the `to` date is inside the range —
    /// the case a naive <c>&lt;= to</c> against midnight would drop.
    /// </summary>
    [Fact]
    public async Task Project_DateRange_ClipsInclusivelyAtBothEnds()
    {
        var user = await NewUserAsync();
        var seed = await SeedWeekAsync(user);

        var monday = await GetAsync<ProjectReportDto>(
            user, $"/reports/projects/{seed.Alpha}?from=2031-03-03&to=2031-03-03");
        var throughTuesday = await GetAsync<ProjectReportDto>(
            user, $"/reports/projects/{seed.Alpha}?from=2031-03-03&to=2031-03-04");
        var endOfDay = await GetAsync<ProjectReportDto>(
            user, $"/reports/projects/{seed.Alpha}?from=2031-03-02&to=2031-03-02");

        Assert.Equal(5400, monday.TotalSeconds);
        Assert.Equal(12600, throughTuesday.TotalSeconds);
        Assert.Equal(new DateOnly(2031, 3, 3), throughTuesday.From);
        Assert.Equal(new DateOnly(2031, 3, 4), throughTuesday.To);

        // The out-of-week entry sits at 23:59 on the 2nd.
        Assert.Equal(1000, endOfDay.TotalSeconds);
    }

    [Fact]
    public async Task Project_OpenEndedRange_ClipsOnTheGivenSideOnly()
    {
        var user = await NewUserAsync();
        var seed = await SeedWeekAsync(user);

        var fromTuesday = await GetAsync<ProjectReportDto>(user, $"/reports/projects/{seed.Alpha}?from=2031-03-04");
        var untilMonday = await GetAsync<ProjectReportDto>(user, $"/reports/projects/{seed.Alpha}?to=2031-03-03");

        Assert.Equal(9200, fromTuesday.TotalSeconds);
        Assert.Null(fromTuesday.To);

        Assert.Equal(6400, untilMonday.TotalSeconds);
        Assert.Null(untilMonday.From);
    }

    [Fact]
    public async Task Project_Csv_HasTheDocumentedColumns()
    {
        var user = await NewUserAsync();
        var seed = await SeedWeekAsync(user);

        var response = await user.Client.GetAsync(
            $"/reports/projects/{seed.Alpha}?from=2031-03-03&to=2031-03-09&format=csv");
        response.EnsureSuccessStatusCode();

        Assert.Equal(
            ["project,task,seconds", "Alpha,Build,7200", "Alpha,Design,5400"],
            await CsvLinesAsync(response));
    }

    [Fact]
    public async Task Project_Unknown_Returns404()
    {
        var user = await NewUserAsync();

        var response = await user.Client.GetAsync($"/reports/projects/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Project_Tombstoned_Returns404()
    {
        var user = await NewUserAsync();
        var seed = await SeedWeekAsync(user);

        var response = await user.Client.GetAsync($"/reports/projects/{seed.DeletedProject}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Project_ReversedRange_Returns400()
    {
        var user = await NewUserAsync();
        var seed = await SeedWeekAsync(user);

        var response = await user.Client.GetAsync(
            $"/reports/projects/{seed.Alpha}?from=2031-03-09&to=2031-03-03");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Task_ReportsADailyBreakdownForThatTaskAlone()
    {
        var user = await NewUserAsync();
        var seed = await SeedWeekAsync(user);

        var report = await GetAsync<TaskReportDto>(
            user, $"/reports/tasks/{seed.Support}?from=2031-03-03&to=2031-03-09");

        Assert.Equal("Support", report.TaskName);
        Assert.Equal("Beta", report.ProjectName);
        Assert.Equal(seed.Beta, report.ProjectSyncId);
        Assert.Equal(6300, report.TotalSeconds);

        // Only days with time — the range can be open-ended, so there is no frame to zero-fill.
        Assert.Equal(
            [(new DateOnly(2031, 3, 6), 5400L), (new DateOnly(2031, 3, 9), 900L)],
            report.Days.Select(d => (d.Date, d.Seconds)));
    }

    [Fact]
    public async Task Task_DateRange_Clips()
    {
        var user = await NewUserAsync();
        var seed = await SeedWeekAsync(user);

        var report = await GetAsync<TaskReportDto>(
            user, $"/reports/tasks/{seed.Support}?from=2031-03-07&to=2031-03-09");

        Assert.Equal(900, report.TotalSeconds);
        Assert.Equal(new DateOnly(2031, 3, 9), Assert.Single(report.Days).Date);
    }

    [Fact]
    public async Task Task_Csv_HasTheDocumentedColumns()
    {
        var user = await NewUserAsync();
        var seed = await SeedWeekAsync(user);

        var response = await user.Client.GetAsync(
            $"/reports/tasks/{seed.Support}?from=2031-03-03&to=2031-03-09&format=csv");
        response.EnsureSuccessStatusCode();

        Assert.Equal(
            ["date,project,task,seconds", "2031-03-06,Beta,Support,5400", "2031-03-09,Beta,Support,900"],
            await CsvLinesAsync(response));
    }

    [Fact]
    public async Task Task_Unknown_Returns404()
    {
        var user = await NewUserAsync();

        var response = await user.Client.GetAsync($"/reports/tasks/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Task_Tombstoned_Returns404()
    {
        var user = await NewUserAsync();
        var seed = await SeedWeekAsync(user);

        var response = await user.Client.GetAsync($"/reports/tasks/{seed.DeletedTask}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// An alive task under a tombstoned project reports nothing, so a 200 with a total of zero would be
    /// true about the number and misleading about the reason.
    /// </summary>
    [Fact]
    public async Task Task_UnderATombstonedProject_Returns404()
    {
        var user = await NewUserAsync();
        var seed = await SeedWeekAsync(user);

        var response = await user.Client.GetAsync($"/reports/tasks/{seed.TaskUnderDeletedProject}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The whole point of scoping on the token claim: two users hold identically-shaped weeks and
    /// neither can see, total, or address the other's rows.
    /// </summary>
    [Fact]
    public async Task Reports_NeverIncludeAnotherUsersData()
    {
        var mine = await NewUserAsync();
        var theirs = await NewUserAsync();

        var seed = await SeedWeekAsync(mine);
        var theirProject = Guid.NewGuid();
        var theirTask = Guid.NewGuid();
        await SaveAsync(
            NewProject(theirs.Id, theirProject, "Confidential"),
            NewTask(theirs.Id, theirTask, theirProject, "Confidential task"),
            NewEntry(theirs.Id, theirTask, At(2031, 3, 3, 9), At(2031, 3, 3, 17), 28800));

        var report = await GetAsync<WeeklyReportDto>(mine, $"/reports/weekly?weekOf={WeekStart:yyyy-MM-dd}");

        Assert.Equal(18900, report.TotalSeconds);
        Assert.DoesNotContain(report.Projects, p => p.ProjectName == "Confidential");

        // Not merely absent from the totals — unaddressable, and told apart from nothing at all.
        Assert.Equal(HttpStatusCode.NotFound, (await mine.Client.GetAsync($"/reports/projects/{theirProject}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await mine.Client.GetAsync($"/reports/tasks/{theirTask}")).StatusCode);

        var theirReport = await GetAsync<WeeklyReportDto>(theirs, $"/reports/weekly?weekOf={WeekStart:yyyy-MM-dd}");
        Assert.Equal(28800, theirReport.TotalSeconds);
        Assert.Equal(HttpStatusCode.NotFound, (await theirs.Client.GetAsync($"/reports/projects/{seed.Alpha}")).StatusCode);
    }

    private sealed record TestUser(int Id, HttpClient Client);

    private sealed record SeededWeek(
        Guid Alpha,
        Guid Beta,
        Guid DeletedProject,
        Guid Design,
        Guid Build,
        Guid Support,
        Guid DeletedTask,
        Guid TaskUnderDeletedProject);

    /// <summary>
    /// A fresh User + Device with a token signed the same way the host signs its own. See the class
    /// note for why these tests cannot use <c>/auth/register-device</c>.
    /// </summary>
    private async Task<TestUser> NewUserAsync()
    {
        var userId = await fixture.NewUserAsync("Reports");
        var device = await fixture.EnrollDeviceAsync(userId, "reports-test");
        return new TestUser(userId, device.Client);
    }

    /// <summary>
    /// The fixture every totals assertion is computed from, by hand:
    ///
    /// <code>
    /// Alpha / Design   Mon 03-03 09:00  3600   ┐ same day + task, must merge to 5400
    ///                  Mon 03-03 13:00  1800   ┘
    /// Alpha / Build    Tue 03-04 10:00  7200
    /// Beta  / Support  Thu 03-06 08:00  5400
    /// Beta  / Support  Sun 03-09 23:30   900     last half-hour of the week
    ///                                  -----
    ///                          in week  18900
    ///
    /// Alpha / Design   Sun 03-02 23:59  1000     one minute before the week
    /// Alpha / Build    Mon 03-10 00:01  2000     one minute after it
    /// Alpha / Design   Wed 03-05 10:00  9999     tombstoned entry
    /// Alpha / Deleted task
    ///                  Wed 03-05 10:00  4444     tombstoned task
    /// Deleted project / Task under deleted project
    ///                  Wed 03-05 10:00  3333     tombstoned project
    /// Alpha / Build    Fri 03-07 09:00     —     still running (no EndTime)
    /// </code>
    ///
    /// Wednesday is where all three tombstones sit, so a leak shows up as a day that should be zero.
    /// </summary>
    private async Task<SeededWeek> SeedWeekAsync(TestUser user)
    {
        var seed = new SeededWeek(
            Alpha: Guid.NewGuid(),
            Beta: Guid.NewGuid(),
            DeletedProject: Guid.NewGuid(),
            Design: Guid.NewGuid(),
            Build: Guid.NewGuid(),
            Support: Guid.NewGuid(),
            DeletedTask: Guid.NewGuid(),
            TaskUnderDeletedProject: Guid.NewGuid());

        await SaveAsync(
            NewProject(user.Id, seed.Alpha, "Alpha"),
            NewProject(user.Id, seed.Beta, "Beta"),
            NewProject(user.Id, seed.DeletedProject, "Deleted project", isDeleted: true),
            NewTask(user.Id, seed.Design, seed.Alpha, "Design"),
            NewTask(user.Id, seed.Build, seed.Alpha, "Build"),
            NewTask(user.Id, seed.Support, seed.Beta, "Support"),
            NewTask(user.Id, seed.DeletedTask, seed.Alpha, "Deleted task", isDeleted: true),
            NewTask(user.Id, seed.TaskUnderDeletedProject, seed.DeletedProject, "Orphan"),
            NewFinished(user.Id, seed.Design, At(2031, 3, 3, 9), 3600),
            NewFinished(user.Id, seed.Design, At(2031, 3, 3, 13), 1800),
            NewFinished(user.Id, seed.Build, At(2031, 3, 4, 10), 7200),
            NewFinished(user.Id, seed.Support, At(2031, 3, 6, 8), 5400),
            NewFinished(user.Id, seed.Support, At(2031, 3, 9, 23, 30), 900),
            NewFinished(user.Id, seed.Design, At(2031, 3, 2, 23, 59), 1000),
            NewFinished(user.Id, seed.Build, At(2031, 3, 10, 0, 1), 2000),
            NewFinished(user.Id, seed.Design, At(2031, 3, 5, 10), 9999, isDeleted: true),
            NewFinished(user.Id, seed.DeletedTask, At(2031, 3, 5, 10), 4444),
            NewFinished(user.Id, seed.TaskUnderDeletedProject, At(2031, 3, 5, 10), 3333),
            NewEntry(user.Id, seed.Build, At(2031, 3, 7, 9), end: null, durationSeconds: null));

        return seed;
    }

    private async Task SaveAsync(params object[] rows)
    {
        using var db = fixture.CreateDbContext();
        db.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private static Project NewProject(int userId, Guid syncId, string name, bool isDeleted = false) => new()
    {
        UserId = userId,
        SyncId = syncId,
        Version = 1,
        Name = name,
        IsDeleted = isDeleted,
        CreatedAt = DateTime.UtcNow,
    };

    private static TaskItem NewTask(int userId, Guid syncId, Guid projectSyncId, string name, bool isDeleted = false) => new()
    {
        UserId = userId,
        SyncId = syncId,
        ProjectSyncId = projectSyncId,
        Version = 1,
        Name = name,
        IsDeleted = isDeleted,
        CreatedAt = DateTime.UtcNow,
    };

    private static TimeEntry NewFinished(int userId, Guid taskSyncId, DateTime start, long seconds, bool isDeleted = false) =>
        NewEntry(userId, taskSyncId, start, start.AddSeconds(seconds), seconds, isDeleted);

    private static TimeEntry NewEntry(
        int userId, Guid taskSyncId, DateTime start, DateTime? end, long? durationSeconds, bool isDeleted = false) => new()
    {
        UserId = userId,
        SyncId = Guid.NewGuid(),
        TaskSyncId = taskSyncId,
        Version = 1,
        StartTime = start,
        EndTime = end,
        DurationSeconds = durationSeconds,
        IsDeleted = isDeleted,
        CreatedAt = DateTime.UtcNow,
    };

    private static DateTime At(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    private static async Task<T> GetAsync<T>(TestUser user, string url)
    {
        var response = await user.Client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<string[]> CsvLinesAsync(HttpResponseMessage response)
    {
        var csv = await response.Content.ReadAsStringAsync();

        // RFC 4180 line endings; the trailing CRLF leaves an empty element that is not a row.
        return csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
    }
}
