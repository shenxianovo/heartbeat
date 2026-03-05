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
- devices (GET)                 // 前端用
  - {deviceId}/status (GET)     // 前端用
  - {deviceId}/usage (GET)      // 前端用
- apps (GET)                    // 前端用
  - {appId}/icon (GET)          // 前端用
  - icon(POST)                  // 客户端用
- usage (POST)                  // 客户端用
- status (POST)                 // 客户端用
```