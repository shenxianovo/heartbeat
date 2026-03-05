# Heartbeat

本人的 Windows PC 应用使用情况监视器  
https://shenxianovo.com/heartbeat

## 项目结构

```
Heartbeat
├─desktop
│  └─Heartbeat.Client       // 客户端，.NET Console
├─frontend                  // 前端，Vue
├─server
│  └─Heartbeat.Server       // 服务端，ASP.Net Core
└─shared
    └─Heartbeat.Core        // 客户端&服务端共享资源，.NET Class Library
```

## API

目前的API...  
下面是自查用的，正式文档等改好了再写...

```
Base URL: https://shenxianovo.com/heartbeat/api/v1
- devices (GET)                 // 前端用：获取设备列表
  - {deviceId} (GET)            // 前端用：获取单个设备信息
  - heartbeat (POST)            // 客户端用：上传设备心跳

- apps (GET)                    // 前端用：获取应用列表
  - {appId}/icon (GET)          // 前端用：获取应用图标
  - icon (POST)                 // 客户端用：上传应用图标

- usage (POST)                  // 客户端用：上传 usage 记录
- usage (GET)                   // 前端用：查询 usage
  - ?deviceId=                  // 设备Id
  - &start=                     // 开始时间
  - &end=                       // 结束时间

- reports                       // 前端用：统计数据
  - daily (GET)                 // 每日使用统计
    - ?deviceId=                // 设备Id 
    - &date=                    // 日期
  - weekly (GET)                // 每周使用统计
    - ?deviceId=                // 设备Id
    - &date=                    // 周内任意一天日期（Mon-Sun
```