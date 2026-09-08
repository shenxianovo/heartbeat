# Dashboard

Dashboard 是回顾与管理入口，以展示为主；它也把用户确认的叙事知识写回 Analytics，并
直连对应 Hub 完成交互授权。Analytics 不代理第三方账号凭据、授权应答或 Hub 管理命令。

## Language

**Dashboard**:
主页面：状态卡、Subject 状态、Recap、当日排行、所在周图表、时间轴、键盘热力图与鼠标活动的组合。30s 轮询报表；`useHeartbeat` 是瘦协调器，组合设备选择 / 在场 / 报表三个数据域。

**Local Calendar Window（本地日历窗口）**:
Dashboard 对所选日期的解释：以当前浏览器 civil timezone 将日或周映射为 `[本地起点, 下一本地起点)` 的事实窗口，周一开周，夏令时切换日可以是 23 或 25 小时。同一窗口是 Report、Recap、Timeline 与 Asking 对“这天 / 这周”的共同含义。
_Avoid_: 固定 24 小时日窗、用单个 UTC offset 代替时区规则、把 Episode / Strand 的 DateOnly 当作查询窗口

**Subject Status Panel（主体状态面板）**:
Dashboard 中按 Subject 展示当下状态的区域：Machine 展示 Current Activity，Account 展示该账号来源能够如实观测的当前状态。需要交互授权时仅向 owner 提供恢复入口；授权不是进入 Dashboard 或查看其他 Subject 的前置条件。
_Avoid_: Device-only Current App Panel、全屏登录门禁、把 Hub 认证阶段显示成业务活动

**Timeline（时间轴）**:
按天回放的注意力线（ADR-019 主视图）：单一时间线跟随 system 前台段。simple / detailed 双模式，detailed 可拖拽缩放、看标题明细。
_Avoid_: Replay 泳道、多轨（那是 ADR-019 的展开态，未建）

**Label Upgrade（标签升级）**:
ADR-019 §2：渲染 system 段时若存在同 App 的重叠插件段，标签由窗口标题升级为插件语义（URL/页面）。按时间窗口判定，无覆盖的时段（含插件安装前全部历史）fallback 到 Title Formatter。纯展示层，不改数据。
已知局限：段选择按时间重叠占比，多浏览器窗口时后台窗口的插件段也是候选，可能升级成未在看的页面——窗口↔前台消歧未建（需要窗口身份与 system 标题的匹配）。

**Lane（泳道）**:
回放展开态里同一 Source 轨内的副本细分：有副本身份的段按身份分稳定泳道（browser 用 `attributes.windowId`），无身份的段贪心装箱兜底（interval packing）。副本身份是采集器侧的 Attributes 约定，展示层经按 source 的提取器注册表读取（`segmentAdapters.laneKeyOf`，Title Formatter 同款模式）。system 前台互斥，恒为单 lane。浏览器 windowId 跨重启可能复用，同 lane 顺序排开，可读性无损。

**Title Formatter（标题归一化）**:
ADR-016 的展示层无损归一化：per-app formatter 把原始窗口标题洗成友好显示（去应用名后缀、去 tab 计数后缀、spinner 归并等）。是 Label Upgrade 缺席时的兜底层。
_Avoid_: 在采集端或服务端做标题清洗（无损原则，展示层是唯一动标题的地方）

**Get Started（引导页）**:
新用户唯一引导入口：登录后无设备强制着陆于此，三步（创建 API Key → 下载客户端 → 连接采集）打通第一条心跳。下载按钮架构感知（默认 x64，Chromium UA-CH 检测到 ARM 切换，另一架构保留手动链接），指向 latest 直链，依赖发版资产命名稳定。
_Avoid_: 在此堆高级配置（上传间隔、采集器管理等）——只放"能跑起来"的必要步骤

**Presence（在场）**:
设备在线状态与当前前台应用：Dashboard 里唯一的“现在时”信息，其余都是回顾。Presence 表面只呈现当前在线的 Device；离线 Device 仍保留为历史查询维度，不作为“当前使用”或实时状态展示。

**Away（离开）**:
ADR-014 的离开段在展示层的形态：特殊 App 标签（`AWAY_APP`），时间轴上显示为离开区间，排行与统计中单列，不与真实应用混排。

**Keyboard Heatmap（键盘热力图）**:
InputEvent 的可视化：按键频次热力图。只消费聚合频次，不展示按键序列（原始序列等价于键盘记录，永不上屏）。

**Mouse Activity（鼠标活动）**:
InputEvent 的聚合计数：左、右、中键点击和向上、向下滚轮，按所选本地日历窗口与设备统计。
_Avoid_: 鼠标移动距离、移动轨迹（未采集）
