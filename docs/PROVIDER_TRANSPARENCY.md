# Provider transparency

PixelForge records what the runtime actually used, not just what the user
selected.

For applicable jobs, the item and history record include:

| Field | Meaning |
|---|---|
| Requested provider | The provider selected in the operation settings |
| Actual provider | The provider reported by the runtime or engine |
| Fallback reason | Why the requested provider was not used |
| Model ID | The model that executed |
| Runtime version | Engine/runtime version where available |
| Warnings | Provider-specific fallback or restriction messages |

AI image upscaling reports ONNX Runtime CPU/DirectML or NCNN/Vulkan. PP-OCRv6
reports its actual detector/recognizer provider and mixed-provider warnings.
llama.cpp advanced OCR reports Vulkan/GPU/CPU from the local server. Video
upscaling reports the provider used by the frame pipeline and interpolation.
Video hardware encoders are reported separately from AI inference providers.

The Diagnostics report includes runtime/provider availability. The queue's
copyable job report includes the per-item provider fields. Unknown values are
reported as unavailable rather than guessed.
