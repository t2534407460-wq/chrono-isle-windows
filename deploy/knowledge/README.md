# 独立项目知识库服务

这是 ChronoIsle 的独立检索服务，不依赖 MaxKB、数据库或向量模型。Windows Obsidian 继续编辑，Linux 保存经过校验的资料快照，多个项目使用独立密钥访问同一资料库。时屿仍使用原有模型配置生成答案，并校验来源引文和资料版本。

## 数据与边界

- 同步根目录固定选择 `E:\Obsidian Vault\项目知识库`；包含笔记与附件，排除 CodexDebug、隐藏目录、插件配置、缓存、回收站及符号链接。
- 检索 Markdown 正文，保留相对路径、标题、行号、修改时间、内容 SHA-256；图片、PDF、lake 原件会同步保存，但不做 OCR 或正文提取。
- 同步清单最多 20,000 个文件、总量 8 GiB、单文件 128 MiB，ZIP 上传最多 2 GiB。检索单篇 2 MiB、总文本 16 Mi 字符、最多 20,000 个片段；超限在结果中显式标记。
- 当前检索为中文分词片段与标识符的关键词排序，最多返回 6 段。它不能保证召回所有语义相关内容；没有可靠证据时应明确答复资料不足。
- 每次发布先检查全部路径、文件大小和哈希，再原子切换 `current.txt`。Linux 对未改文件建立硬链接；失败不会切换当前版本。一个服务实例负责写入；CLI 导入只用于服务停止时。
- 历史快照保留，旧回答链接可继续读取。没有自动删除历史的任务；应监测磁盘并备份 `data`。清理旧版本将使相应旧回答链接失效。

## 构建和部署

在仓库根目录执行：

```powershell
dotnet publish src/ChronoIsle.Knowledge.Server -c Release -o deploy/knowledge/publish
dotnet publish tools/ChronoIsle.Knowledge.Sync -c Release -r win-x64 --self-contained true -o deploy/knowledge/sync
```

将 Dockerfile、compose.yaml、publish 目录上传到 Linux 独立目录。复制 secrets.example.json 为 secrets.json，配置每个项目随机访问密钥的 SHA-256（32 字节以上随机值；不能使用示例占位符）。只授予同步程序 CanSync=true。原始密钥只交给对应项目的服务端或受保护的本机配置，不能写入网页前端、仓库、URL 或日志。

```sh
mkdir -p data
chown 1654:1654 data
chmod 750 data
chmod 640 secrets.json
chown root:1654 secrets.json
docker compose build
docker compose up -d
docker compose ps
```

默认容器仅监听宿主机 `127.0.0.1:18765`。通过现有 OpenResty 反向代理提供有效 HTTPS，保留 ACME 验证路由；HTTP 不应转发知识库接口。同步路由需要 `client_max_body_size 2g`、30 分钟读写超时及关闭代理请求缓冲，避免重复缓存大文件。接口密钥不能通过明文 HTTP 发送；客户端拒绝重定向和无效证书。1Panel 证书应开启自动续签并确认续签后的站点证书更新。

首次离线同步包：

```powershell
dotnet run --project tools/ChronoIsle.Knowledge.Sync -c Release -- --pack 'E:\Obsidian Vault\项目知识库' project-vault.zip
```

将压缩包上传到独立部署目录，停机导入：

```sh
docker compose stop
docker compose run --rm -v "$PWD/project-vault.zip:/import.zip:ro" knowledge --import /import.zip --data /data
docker compose up -d
```

按需同步使用 `ChronoIsle.Knowledge.Sync.exe --config <配置文件>`，配置 JSON 为 `vaultPath`、`baseUrl`、`protectedKey`。protectedKey 是当前 Windows 用户通过 DPAPI 加密的原始同步密钥（UTF-8 字节再加密、Base64 编码），配置不可跨用户直接复用。任务以同一用户运行，不允许同任务并行；不变文件不会上传。任务仅供知识库 Skill 手动触发，不配置周期、登录或其他自动触发器。程序按需连接，请求关闭 HTTP 持久连接，同步完成或失败后释放客户端并退出；不后台驻留或自动重试。非零退出码表示本轮未确认成功，应检查日志和上次成功时间。

Windows 计划任务应通过无窗口的 `wscript.exe` 启动同目录的 `run-sync-hidden.vbs`，由它以隐藏窗口方式启动 PowerShell 7 的 `run-sync.ps1`。直接把 `pwsh.exe -WindowStyle Hidden` 配为任务动作，控制台可能在 PowerShell 解析参数前闪现。将两个脚本与同步程序部署在同一目录，任务动作使用：

```text
程序：C:\Windows\System32\wscript.exe
参数：//B //NoLogo "<同步目录>\run-sync-hidden.vbs"
起始于：<同步目录>
```

启动器等待同步结束、返回原退出码并退出；保留不允许重叠及运行时限，日志与 `status.json` 仍由原 PowerShell 脚本写入。保留任务的按需启动能力，移除全部自动触发器；不要禁用任务导致 Skill 无法手动启动。该启动方式需要本机启用 Windows Script Host/VBScript，不更改系统脚本策略。远程知识库查询接口保持可用，关闭的是本机客户端连接和本轮同步进程。

时屿在“设置 → Obsidian 知识库”启用远程模式，填写 HTTPS 根地址和该项目的读取密钥。点击测试确认服务已初始化，再保存；原有本地目录设置保留。其他项目按下面契约集成。

## API v1

除 `/health` 外使用 `Authorization: Bearer <项目密钥>`；JSON 使用 camelCase。密钥通过环境/密钥管理传入调用方，不在 URL 中出现。

| 方法与路径 | 输入 / 输出 | 权限 |
| --- | --- | --- |
| GET `/health` | 服务存活与 apiVersion，不暴露资料 | 无 |
| GET `/v1/status` | `{ready, apiVersion}` | 读取 |
| POST `/v1/search` | `{question}` → `{snapshotId,sources,noteCount,skippedCount,limited,vaultPath}` | 读取 |
| POST `/v1/verify` | `{snapshotId,sources:[{relativePath,contentHash}]}` → `{unchanged}` | 读取 |
| GET `/v1/files/{snapshotId}/{编码后的相对路径}` | 指定快照的 Markdown、图片或 PDF，最多 16 MiB | 读取 |
| GET `/v1/sync/manifest` | `{snapshotId,files}` | 同步 |
| POST `/v1/sync` | Content-Type: application/zip；校验后返回新清单 | 同步 |

search 的每个 source 包括 id（S1…S6）、relativePath、heading、startLine、endLine、text、metadata、modifiedUtc、contentHash。调用方用自己配置的 HTTPS 根地址构建文件链接，不接受服务返回的任意跳转地址；路径每一段分别 URL 编码。模型调用前后用 verify 确认资料版本不变，并校验答案的引用实际包含于 source.text；版本变化时重新检索。API 返回的是证据，不是服务端生成的最终答案。

```sh
curl --fail-with-body "$KNOWLEDGE_URL/v1/search" \
  -H "Authorization: Bearer $KNOWLEDGE_KEY" \
  -H 'Content-Type: application/json' \
  --data '{"question":"恢复数据库前需要做什么？"}'
```

状态码：400 输入/同步包无效；401 密钥缺失或无效；403 无同步权限；404 资料不存在或类型不支持；409 并发发布或版本冲突；413 超限；415 上传类型错误；429 并发/频率超限；503 资料暂时不可读；504 处理超时。调用方应有超时，429/503 可有限退避重试，400/401/403 不应无限重试。每个密钥限 120 次/分钟，读取并发最多 4。

## 运维与验证

更换密钥时先更新对应 SHA-256 配置并重启服务，再更新调用方；每个项目独立配置以便撤销。备份 data 与哈希配置、保护 Windows 加密配置。恢复到新的服务器后检查 UID 1654 的目录权限、TLS 和防火墙。升级先备份，保留旧镜像及配置，以健康检查、鉴权、真实搜索、来源图片、完整清单验证后切换。

服务使用 [.NET 10 LTS](https://dotnet.microsoft.com/en-us/platform/support/policy)。容器限制权限和资源，日志轮转，失败自动重启；这些措施不能代替备份和实机验证。当前服务器的公网 IP 80 端口被上游限制，实际使用用户指定的已备案域名 `zhuisu.leadjet.com.cn` 和普通 Let's Encrypt 域名证书。已验证域名的 HTTP ACME 路径可从公网访问，HTTPS 正常验证证书；1Panel 开启证书自动续签。

该域名已有其他业务，保留既有 HTTP 和代理规则。新增 `nginx-location.conf.example` 中的 `/knowledge/` 路径，并在该路径拒绝 HTTP 请求。不要为整个共享域名额外强制 HSTS，以免影响其现有 HTTP 非标准端口。对外基础地址为 `https://zhuisu.leadjet.com.cn/knowledge/`。
