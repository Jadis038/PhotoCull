# 性能基准（--bench 实测）

## 环境

- Windows 11 x64 虚拟机（NT 10.0.26200），4 vCPU，D 盘（系统盘 C:）
- .NET 8.0.428 自包含发布版 `影·筛.exe`
- 测试数据：`C:\Users\16774\ys_bench\farm2000`，**2000 张 Nikon D750 NEF（21.3MB）+ 2000 张同名 JPG**，
  共 4000 个条目（用硬链接构造，磁盘实际占用约 44MB，避免写 44GB 垃圾；**说明**：硬链接共享 inode，
  文件系统缓存会重复命中，绝对时间比真实 2000×50MB 场景乐观，但解码/索引/调度路径完全一致）
- 样本里没有 Nikon Z8 的 NEF（客户目标机型），45MP 相关数字按像素数外推

## 冷缓存结果（`--bench ... --cold`，CSV 见 `bench_farm2000_cold.csv`）

| 阶段 | 指标 | 实测 | 目标 | 结论 |
|---|---|---|---|---|
| enumerate | 打开文件夹 → 文件名列表可见（4000 条） | **8 ms** | < 1000 ms | ✅ 125× 余量 |
| first_screen | 首屏 20 张缩略图 | **194 ms** | < 2000 ms | ✅ |
| switch_first | 单击切图（首次）P50 / P95 | **23.6 / 29.4 ms** | < 400 ms | ✅ |
| switch_cached | 单击切图（缓存命中）P50 / P95 | **0.0 / 0.1 ms** | < 100 ms | ✅ |
| full_decode | 100% 全解（D750，6032×4032） | **928 ms** | 后台 < 3000 ms | ✅ |
| warm_all | 全量缩略图 4000 张 / 折算 2000 张 | **16.6 s / 8.3 s** | < 180 s | ✅ |
| decode | 单张解码 P50 / P95 | **14 / 23 ms** | — | — |
| memory | 进程内存峰值 | **574 MB** | < 1200 MB | ✅ |

全部指标达标。内存这一项在开发中踩到过一个真实的坑：最初 `BitmapImage` 直接持有源 `MemoryStream`，
4000 张后进程常驻 3.7GB；改成"像素复制到独立 `BitmapSource`"后降到 574MB（详见 `docs/SPEC.md` §5）。

## 45MP（Nikon Z8）外推

样本里最大的全解是 Google Pixel 8 Pro.DNG（8160×6144，45MP 级）：**1671 ms**；
DJI Mavic 4 Cine.DNG（8192×4608）：**1398 ms**；OPPO Find X10 Pro Max（8192×6144）：**2067 ms**。
按像素线性外推，Z8（8256×5504 ≈ 45.4MP）全解约 **1.8~2.1 s**，仍在 3s 内。
缩略图路径只读内嵌 JPEG（Z8 的 NEF 内嵌全尺寸 JPEG，约 6~8MB），单张 open+unpack_thumb 预计
20~40ms（取决于冷盘），与机型像素无关。

## 复跑方式

```powershell
# 1) 造测试数据（硬链接，节省磁盘）
#    把 NEF 复制两份放到 C:\Users\16774\ys_bench\src\，再各建 1000 个硬链接
# 2) 冷启动基准
影·筛.exe --bench "C:\Users\16774\ys_bench\farm2000" --cold --csv bench.csv
# 3) 热缓存基准（不 --cold，观察缓存命中率）
影·筛.exe --bench "C:\Users\16774\ys_bench\farm2000" --csv bench_warm.csv
```

CSV 列：`stage,metric,value,unit,note`，可直接进 Excel。
