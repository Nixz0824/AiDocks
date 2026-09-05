# LineDock（AI 网络）

Windows 边缘停靠的海外 AI 线路监测。界面中文文案保持中文。贴边形状是内侧 S 刘海，不是胶囊。

源码在本目录。成品 exe 放到产品线 `../dist/LineDock.exe`。

## 发布

1. 先结束正在运行的 LineDock。
2. `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish`
3. 复制 `publish\LineDock.exe` 到 `..\dist\LineDock.exe`。
