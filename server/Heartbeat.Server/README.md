改Entities后请执行：

``` bash
dotnet ef migrations add xxx
dotnet ef database update
```

数据表是由EFCore生成的（严格来说是EFCore 生成 Migrations，Migrations再改数据表）