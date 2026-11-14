using PixelForge.Domain;
using PixelForge.Domain.Models;
using Xunit;

namespace PixelForge.Tests.Unit;

public sealed class JobModelTests
{
    private static ProcessingJob CreateJob(JobState state = JobState.Pending, params JobItemState[] itemStates)
    {
        var job = new ProcessingJob
        {
            Operation = OperationKind.ImageConvert,
            Title = "Test batch",
            State = state,
            Settings = new ImageConversionSettings { OutputDirectory = @"C:\out" }
        };
        for (var i = 0; i < itemStates.Length; i++)
        {
            job.Items.Add(new JobItem
            {
                JobId = job.Id,
                InputPath = $@"C:\in\file{i}.png",
                FileName = $"file{i}.png",
                State = itemStates[i],
                Progress = itemStates[i] == JobItemState.Running ? 0.5 : 0,
                OutputBytes = itemStates[i] == JobItemState.Completed ? 1024 : null
            });
        }
        return job;
    }

    [Fact]
    public void Counts_and_progress_reflect_item_states()
    {
        var job = CreateJob(JobState.Running,
            JobItemState.Completed, JobItemState.Running, JobItemState.Failed, JobItemState.Pending);

        Assert.Equal(4, job.TotalItems);
        Assert.Equal(1, job.CompletedItems);
        Assert.Equal(1, job.FailedItems);
        Assert.Equal(2, job.FinishedItems);
        Assert.Equal(0, job.SkippedItems + job.CancelledItems);
        Assert.Equal(1, job.TotalOutputBytes / 1024);
        Assert.Equal(0.625, job.Progress, 3);
        Assert.Contains("2 of 4", job.ProgressText);
        Assert.EndsWith("%", job.ProgressText);
    }

    [Theory]
    [InlineData(JobState.Pending, false, false, true)]
    [InlineData(JobState.Preparing, false, false, false)]
    [InlineData(JobState.Running, false, false, false)]
    [InlineData(JobState.Completed, true, false, false)]
    [InlineData(JobState.Failed, true, true, false)]
    [InlineData(JobState.Cancelled, true, true, false)]
    [InlineData(JobState.Interrupted, true, true, false)]
    public void State_flags_are_consistent(JobState state, bool terminal, bool canRetry, bool canRemove)
    {
        var job = CreateJob(state);
        Assert.Equal(terminal, job.IsTerminal);
        Assert.Equal(canRetry, job.CanRetry);
        Assert.Equal(canRemove, job.CanRemove);
        Assert.Equal(state is JobState.Pending or JobState.Preparing or JobState.Running, job.CanCancel);
    }

    [Fact]
    public void Completed_batch_with_failures_reports_partial_failure()
    {
        var job = CreateJob(JobState.Completed, JobItemState.Completed, JobItemState.Failed);
        Assert.Equal("Completed with 1 failure", job.StateText);
        Assert.Equal("Image conversion", job.OperationText);
    }

    [Theory]
    [InlineData(OperationKind.ImageConvert, "Image conversion")]
    [InlineData(OperationKind.AudioConvert, "Audio conversion")]
    [InlineData(OperationKind.VideoConvert, "Video conversion")]
    [InlineData(OperationKind.UpscaleTraditional, "Image upscaling")]
    [InlineData(OperationKind.UpscaleAi, "AI upscaling")]
    public void Operation_text_matches_operation(OperationKind operation, string expected)
    {
        var job = new ProcessingJob
        {
            Operation = operation,
            Title = "title",
            Settings = new ImageConversionSettings()
        };
        Assert.Equal(expected, job.OperationText);
    }

    [Theory]
    [InlineData(JobItemState.Pending, "Waiting")]
    [InlineData(JobItemState.Completed, "Done")]
    [InlineData(JobItemState.Failed, "Failed")]
    [InlineData(JobItemState.Cancelled, "Cancelled")]
    [InlineData(JobItemState.Skipped, "Skipped")]
    public void Item_state_text_is_clear(JobItemState state, string expected)
    {
        var item = new JobItem
        {
            InputPath = @"C:\in\a.png",
            FileName = "a.png",
            State = state
        };
        Assert.Equal(expected, item.StateText);
    }

    [Fact]
    public void Empty_job_has_zero_progress_without_division_by_zero()
    {
        var job = CreateJob(JobState.Pending);
        Assert.Equal(0, job.Progress);
        Assert.Contains("0 of 0", job.ProgressText);
    }
}
