这个项目是wishGI光照烘焙在Unity3d游戏引擎上的实现
1️⃣ Offline（离线系统）——“数学世界”

原则：

不知道 Unity 是什么

不关心 Shader

只关心：点、方向、矩阵、SH
2️⃣ Data（中间态）——“契约层”

这是整个项目最重要、也最容易被忽略的一层。

原则

Unity 和 Offline 都不拥有格式解释权

Data 是双方的“协议”

建议格式

.json（调试阶段）

.bin（后期）
3️⃣ UnityProject（运行时）——“傻瓜消费者”

Unity 的唯一职责：

“按规矩把数据喂给 GPU”