# Spike S1 / S2 报告（LibRaw 能力与耗时）

环境：LibRaw **0.21.1-Release**（vcpkg 预编译，`raw_r.dll` + `jpeg8.dll` + `lcms2.dll` + `zlib1.dll`），
Windows 11 x64，C 盘样本（桌面「逆向」目录）。原始 spike 工程：`spikes/LibRawProbe/`。
测量都用同一进程内 warm cache，第一行是首次（含冷读）。

## S1：单张 RAW 的 open / 内嵌 JPEG / 完整 demosaic

目标：**open + unpack_thumb + make_mem_thumb < 40ms/张**。

| 样本 | 大小 | open(ms) | 内嵌预览(ms) | 预览类型 | 完整 demosaic(ms) | 输出尺寸 |
|---|---|---|---|---|---|---|
| Nikon D750.NEF | 21.3MB | 0.6~5.9 | 0.5~1.3 | JPEG 827KB | **770** | 6032×4032 |
| Sony A7R II.ARW | 41.0MB | 0.8 | 0.4 | JPEG 412KB | **906** | 7968×5320 |
| Canon EOS R5.CR3 | 38.8MB | 0.9 | 2.0 | JPEG 3.0MB | **888** | 8191×5463 |
| Canon EOS 5D Mark III.CR2 | 36.3MB | 0.8 | 2.1 | JPEG 3.1MB | **899** | 5796×3870 |
| OPPO Find X10 Pro Max.dng | 49.2MB | 0.7 | 4.8 | JPEG 7.3MB | **1905** | 8192×6144 |

结论：**open + 内嵌 JPEG 抽取只要 1~6ms，远低于 40ms 目标**（磁盘冷读时主要成本是读那 0.8~7MB 的 JPEG 数据，
按 SSD 500MB/s 估算仍在 ~20ms 以内）；完整 demosaic 是 0.8~2.0 秒，**两者差 300~1000 倍**，
这就是"缩略图绝不能走全解"的量化依据。

## S2：格式覆盖（34 个真实样本）

| 结果 | 数量 | 说明 |
|---|---|---|
| 打开成功 | **32/34** | CR2/CR3/NEF/ARW/RAF/ORF/RW2/PEF/DNG/SRW + 3FR/MEF/MOS/MRW/ERF/DCR |
| 打开失败 | **2/34** | Polaroid x530.X3F、Sigma dp2 Quattro.X3F（LibRaw：`Unsupported file format`） |
| 内嵌 JPEG 预览 | 22 | 大 JPEG（42KB~12.7MB），可直接给 WIC 缩放到 160/1024 |
| 内嵌未压缩位图 | 4 | Hasselblad H3D-39II（320×240）、Kodak DCS Pro（750×1126）、Mamiya ZD（992×1328）、Nokia Lumia 1020（880×656） |
| 无任何内嵌预览 | 10 | 多为手机 DNG（ASUS/HTC/Huawei/LG/OnePlus/Realme/Ricoh/Xiaomi/Insta360）→ 需要 demosaic 兜底 |

元数据（机型/ISO/光圈/快门/焦距/镜头/拍摄时间/尺寸）用同一次 open 的
`libraw_get_iparams / libraw_get_imgother / libraw_get_lensinfo` 读取，**不需要二次打开文件**；
LibRaw 缺的镜头名/色彩空间再从前一步拿到的内嵌 JPEG 的 EXIF 补（同一份内存数据）。

## 两个实现坑（spike 阶段就踩到，正式代码已规避）

1. `libraw_processed_image_t` 的像素数据是**内联在结构体尾部的柔性数组**（`unsigned char data[1]`），
   不是指针。按"指针字段"解析会读到 JPEG 数据本身当指针 → `Marshal.Copy` 直接崩 CLR。
   正确做法：数据起始地址 = 结构体指针 + 16 字节。
2. DNG 的内嵌预览常常**不带 EXIF 方向/镜头信息**，`libraw_unpack_thumb` 也可能报
   "No thumbnail in file"；所以必须有兜底路径，并把慢路径标记出来（否则首屏会被几个手机 DNG 拖慢）。
