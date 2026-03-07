这里为您总结了论文《WishGI: Lightweight Static Global Illumination Baking via Spherical Harmonics Fitting》的核心内容、实现细节及相关公式 。

# WishGI：基于球谐函数拟合的轻量级静态全局光照烘焙

本论文提出了一种专为低端平台（如移动设备）设计的静态全局光照（Global Illumination, GI）重建与烘焙方法 。与严重依赖纹理存储和大量像素级纹理采样的主流行业方法不同，WishGI 在显著降低内存使用（仅占主流技术的约 5%）和保障运行时高性能的同时，避免了额外的渲染通道（Render Passes），使其高度兼容前向渲染 。

---

## 核心方法与公式

### 1. 高性能光照重建 (High-performance illumination reconstruction)

为降低片段着色器（Fragment Shader）的性能开销，WishGI 提出了一种基于顶点的光照模型 。

* 将多组球谐函数（Probes）视为高维基函数，顶点处的球谐值 $SH_{v_i}$ 表示为探针球谐值 $SH_i$ 的线性组合 。其关联参数 $\mathcal{A}$ 记录了顶点与探针的索引及权重 ：



$$SH_{v_i} = w_jSH_j + w_kSH_k + \dots + w_nSH_n$$





* 对于网格表面上的任意点 $\mathbf{p}$，通过其所在三角形的三个顶点 $v_a, v_b, v_c$ 的重心坐标 $b = (b_{p,a}, b_{p,b}, b_{p,c})$ 进行插值 。光照重建公式定义为：



$$\hat{f}(\mathcal{A}; \mathbf{p}) = \frac{1}{\pi} \sum_{l=0}^{\infty} \sum_{m=-l}^{l} (b_{p,a}SH_{v_a} + b_{p,b}SH_{v_b} + b_{p,c}SH_{v_c})_l^m T(Y_l^m(\mathbf{d}))$$





* 该计算可直接在基础渲染通道（Base Pass）中完成，极好地适应了移动芯片上的基于图块的延迟渲染 (TBDR) 架构 。



### 2. 高效光照烘焙 (Effective illumination baking)

为了准确表现物体表面的半球形光照，WishGI 仅拟合表面有效采样点方向上的光照信息 。

* 
**方向权重**：优先保证几何法线方向的光照精度，方向权重定义为 $w(\mathbf{d}) = \max(0, \cos(\mathbf{d}, \mathbf{n}))$ 。光照误差损失函数为：



$$E_{light} = \int_{S} \left( \int_{\Omega} w(\mathbf{d}) \left( \hat{f}(\mathcal{A}; \mathbf{p}, \mathbf{d}) - f(\mathbf{p}, \mathbf{d}) \right)^2 d\mathbf{d} \right) d\mathbf{p}$$





* 
**平滑正则化**：全局光照通常是低频且平滑的，因此引入正则化项以约束法线方向的梯度 ：



$$E_{reg} = \sum_{\mathbf{v} \in S} ||\nabla\hat{f}(\mathcal{A}; \mathbf{v}, \mathbf{n})||^2$$





* 
**线性回归求解**：最终的优化目标结合了光照损失与正则化项，可转化为一个经典的线性回归问题求得全局最优解 ：



$$( (\mathbf{w} \cdot T(Y) \cdot \mathbf{B} \cdot \mathbf{W})^T (\mathbf{w} \cdot T(Y) \cdot \mathbf{B} \cdot \mathbf{W}) + \lambda ( (Y' \mathbf{W})^T \mathbf{D}^T \mathbf{D} (Y' \mathbf{W}) ) ) \mathbf{SH} = (\mathbf{w} \cdot T(Y) \cdot \mathbf{B} \cdot \mathbf{W})^T \cdot (\mathbf{w} \cdot \mathbf{I})$$






### 3. 逆向探针分布 (Inverse probe distribution)

有别于在整个场景中放置探针导致冗余的传统方法，WishGI 在**局部空间**中为每个网格单独执行探针分布，以离线优化的方式生成关联参数 。* 为解决探针方法常见的漏光（Light Leakage）问题，采用 K-Medoids 聚类方法结合几何先验初始化探针 ：


$$E = \sum_{i=1}^{K} \sum_{\mathbf{p} \in C_j} \text{dist}(\mathbf{p}, o_i)$$



* 
**距离度量优化**：结合 A* 和 Dijkstra 算法。若两点相互可见则使用欧氏距离；若不可见，则通过寻路算法计算距离，从而合理增加被遮挡采样点的距离以避免漏光 。


* 采样点权重随后通过重心坐标转移至顶点 。



---

## 工程实现细节 (Implementation Details)

* 
**系数编码与压缩**：使用 RGB 通道中的最大绝对值作为乘数（占用 1 字节）对其他系数进行归一化 。一阶和二阶系数使用 12 个 10-bits 存储，三阶系数使用 15 个 8-bits 存储（总共 16 字节的像素）。这一设计使得仅需一次乘法即可解码数据，同时无缝支持细节级别 (LOD) 。


* 
**零 UV 依赖与内存复用**：算法完全基于几何顶点，摒弃了 UV 映射，这不仅避免了 UV 接缝导致的内存浪费，还节省了制作时间 。顶点相关的索引和权重存储直接替换了模型原有的 `UV2` 通道内存，引入了“零”额外内存开销 。


* 
**采样密度与探针数量**：使用蓝噪声采样（Blue Noise Sampling），标准密度设定为 100 点/平方米 。每个顶点关联 2 个探针，复杂物体最多 256 个探针即可满足高精度表示（多数情况低于 30 个）。


* 
**优化超参数**：平滑正则化权重 $\lambda$ 设为 0.1，烘焙阶段进行 960 个方向的球面采样 。基于 LibTorch 使用 Adam 优化器，学习率由 0.01 衰减至 0.001，迭代 400 次 。



---

## 评估指标与结果 (Results & Advantages)

* 
**定量评估 (mRMSE)**：提出多方向均方根误差指标，评估几何法线半球内的光照质量 。



$$\text{mRMSE} = \sqrt{ \sum_{i=1}^{N_s} \left( \frac{area_i}{Area} \int_{\Omega} \frac{\cos(\theta)}{Cos} \left( f(\mathbf{d}) - \hat{f}(\mathbf{d}) \right)^2 d\mathbf{d} \right) }$$





* 
**性能表现**：在移动设备（如高通骁龙 888）上测试，有效减轻了片段着色器的负担，在保障极高视觉保真度的同时，内存使用大幅低于体积光照贴图 (VLM) 及方向光照贴图 。


* 
**昼夜交替支持 (Time of Day)**：因为其极小的纹理尺寸存储，可通过对整个场景中的不同探针贴图（Probemap）进行插值，以极低开销实现全天候昼夜光照过渡（例如 8 个时间点仅需 4MB 内存）。



### 局限性 (Limitations)

1. 
**动态物体**：目前主要针对静态物体，动态物体需依赖周围环境的球谐值插值 。


2. 
**潜在的 LOD 突跳 (Popping)**：直接将探针分布应用于各个级别的网格，可能会引起 LOD 切换时的视觉跳跃 。


3. 
**高频细节缺失**：受限于球谐函数和顶点-探针结构，较难表现诸如锐利阴影等高频细节 。



---

如果您需要更深入了解论文中特定部分的原理（例如 K-Medoids 聚类寻路的具体细节），或是想要将其方法转换伪代码，我可以继续为您解答。