import sys, hashlib, collections
import numpy as np
import onnx
import onnxruntime as ort

def tensor_hash(arr):
    a = np.ascontiguousarray(arr.astype(np.float32))
    return hashlib.sha256(a.tobytes()).hexdigest()[:16], a.shape

def inspect(path, run=False):
    m = onnx.load(path)
    print(f"=== {path} ===")
    print("opset:", [(o.domain or 'ai.onnx', o.version) for o in m.opset_import])
    for i in m.graph.input:
        dims = [d.dim_value if d.dim_value else d.dim_param for d in i.type.tensor_type.shape.dim]
        print("input:", i.name, dims, "dtype", i.type.tensor_type.elem_type)
    for o in m.graph.output:
        dims = [d.dim_value if d.dim_value else d.dim_param for d in o.type.tensor_type.shape.dim]
        print("output:", o.name, dims, "dtype", o.type.tensor_type.elem_type)
    print("initializers:", len(m.graph.initializer), "total params:", sum(np.prod(t.dims) for t in m.graph.initializer))
    if run:
        s = ort.InferenceSession(path, providers=['CPUExecutionProvider'])
        x = np.zeros((1,3,16,24), dtype=np.float32)
        y = s.run(None, {s.get_inputs()[0].name: x})[0]
        print("actual output shape for 16x24:", y.shape, "range", float(y.min()), float(y.max()))

for p in sys.argv[1:]:
    inspect(p, run=True)

# compare anime onnx weights with official pth
import torch
pth = 'third_party/models/downloads/RealESRGAN_x4plus_anime_6B.pth'
onnx_path = 'third_party/models/downloads/realesrgan_anime6b.onnx'
ck = torch.load(pth, map_location='cpu', weights_only=False)
sd = ck.get('params_ema') or ck.get('params') or ck
print("=== PTH state dict ===", type(sd), len(sd))
pth_hashes = collections.Counter()
pth_shapes = collections.Counter()
for k,v in sd.items():
    if hasattr(v, 'detach'):
        arr = v.detach().cpu().numpy()
        h, shp = tensor_hash(arr)
        pth_hashes[h]+=1
        pth_shapes[shp]+=1
print("pth tensor count", sum(pth_hashes.values()), "unique hashes", len(pth_hashes))
m = onnx.load(onnx_path)
onnx_hashes = collections.Counter()
for t in m.graph.initializer:
    arr = onnx.numpy_helper.to_array(t)
    h, shp = tensor_hash(arr)
    onnx_hashes[h]+=1
print("onnx tensor count", sum(onnx_hashes.values()), "unique hashes", len(onnx_hashes))
matched = sum((pth_hashes & onnx_hashes).values())
print("exact hash matches:", matched, "of", sum(pth_hashes.values()))
missing = pth_hashes - onnx_hashes
print("pth hashes not found in onnx:", sum(missing.values()))
extra = onnx_hashes - pth_hashes
print("onnx hashes not in pth:", sum(extra.values()))
