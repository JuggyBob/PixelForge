namespace PixelForge.Presentation.Services;

public enum ModelPanelFilter
{
    All,
    ImageUpscaling,
    VideoUpscaling,
    FrameInterpolation,
    Ocr
}

public sealed record ModelFilterOption(ModelPanelFilter Value, string Label)
{
    public override string ToString() => Label;
}

public interface IModelPanelService
{
    bool IsOpen { get; }
    ModelPanelFilter Filter { get; }
    event EventHandler<ModelPanelFilter>? OpenRequested;
    event EventHandler? CloseRequested;
    void Show(ModelPanelFilter filter = ModelPanelFilter.All);
    void Hide();
}

public sealed class ModelPanelService : IModelPanelService
{
    public bool IsOpen { get; private set; }
    public ModelPanelFilter Filter { get; private set; } = ModelPanelFilter.All;
    public event EventHandler<ModelPanelFilter>? OpenRequested;
    public event EventHandler? CloseRequested;

    public void Show(ModelPanelFilter filter = ModelPanelFilter.All)
    {
        Filter = filter;
        IsOpen = true;
        OpenRequested?.Invoke(this, filter);
    }

    public void Hide()
    {
        IsOpen = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
