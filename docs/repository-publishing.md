# 源码同步与公开发行

2026-10-05 用户要求源码在 Gitee、GitHub 同时存储。得知 Gitee 已公开后，用户明确改为“都开放”：两个源码仓库均公开，替代此前只公开发行包的可见性要求；独立发行仓库继续仅存 README、安装包和发行说明。

| 用途 | 仓库 | 可见性 |
| --- | --- | --- |
| Gitee 源码 | https://gitee.com/Tr11111/chrono-isle-windows | 公开 |
| GitHub 源码 | https://github.com/t2534407460-wq/chrono-isle-windows | 公开 |
| 公开发行 | https://github.com/t2534407460-wq/chrono-isle-windows-releases | 公开；文件树仅 README |

## 后续提交

本机配置保存在共享 Git 配置中，对当前仓库所有 worktree 生效。`origin` 从 Gitee 拉取，向 Gitee 和 GitHub 推送；`github` 用于单独读取和核验 GitHub。

新克隆执行：

```powershell
git remote set-url origin https://gitee.com/Tr11111/chrono-isle-windows.git
git config --replace-all remote.origin.pushurl https://gitee.com/Tr11111/chrono-isle-windows.git
git remote set-url --add --push origin https://t2534407460-wq@github.com/t2534407460-wq/chrono-isle-windows.git
git remote add github https://t2534407460-wq@github.com/t2534407460-wq/chrono-isle-windows.git
git config remote.pushDefault origin
```

如果 `github` 已存在，使用 `git remote set-url github` 更新地址。账号名前缀用于选择 Git Credential Manager 中的正确账号，不包含令牌。

提交后正常执行 `git push origin`；首次推送新分支使用 `git push -u origin <当前分支>`。分别执行 `git ls-remote` 核对两个源码仓库中目标分支 SHA 与本地相同。双推送依次执行，不具备跨平台事务；任一端失败时保留成功端，修复失败原因后重试同一提交，不强制推送。

## 公开发行

发行账号、草稿核验和公开范围沿用 21day。公开发行仓库只能包含 README 和发行说明，不能从源码仓库推送分支、标签或历史；公开 Release 的 tag 基于其独立 README 历史。

安装包沿用本项目已有构建和签名状态。固定文件后使用同一二进制上传，不因本次仓库配置重新构建 0.4.6。源码提交不等于用户人工验收；发行说明保持“已修改待测试”。

```powershell
python scripts/publish-release.py --version 0.4.6 --installer releases/ChronoIsle-Setup-0.4.6-win-x64.exe --notes releases/README.md
if ($LASTEXITCODE -ne 0) { throw '草稿上传或核验失败' }
python scripts/publish-release.py --version 0.4.6 --installer releases/ChronoIsle-Setup-0.4.6-win-x64.exe --notes releases/README.md --publish
if ($LASTEXITCODE -ne 0) { throw '发行失败' }
```

脚本仅上传指定安装包与该版本的 `SHA256SUMS.txt`，核验资产名、大小、digest 和发行仓库文件树。凭据从指定账号已有 Git Credential Manager 登录读取，或在 CI 中从 `CHRONOISLE_RELEASE_TOKEN` 获取；不得把令牌写入文件或日志。

当前采用本地验证后发行，GitHub 源码仓库的 Actions 保持关闭，不额外配置自动任务或发行密钥。若以后启用现有 Release 工作流，需要专用的 `CHRONOISLE_RELEASE_TOKEN` secret，写入独立发行仓库；普通 `GITHUB_TOKEN` 不能跨仓库发行。
