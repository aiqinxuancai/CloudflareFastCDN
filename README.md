# CloudflareFastCDN

自动优选 Cloudflare CDN IP 并更新 DNS A 记录。支持 Cloudflare、腾讯云 DNSPod、阿里云解析。

推荐阅读：[试试Cloudflare IP优选！让Cloudflare在国内再也不是减速器！](https://2x.nz/posts/cf-fastip/)

## 工作原理

1. 首次启动解析已排名域名的当前 IPv4 地址并各做一次 HTTP 检查，全部正常时跳过首次全量优选
2. 需要全量优选时，从 Cloudflare IPv4 网段按 `/24` 子网抽样
3. 多轮 ICMP Ping 筛选低延迟候选 IP
4. HTTP 连通性验证，可选带宽测速（带宽优先）
5. 子网缓存（`subnet_cache.json`）加速历史优质节点复用
6. 按「更新排名」把第 N 名的 IP 写入 TopN 名次中所有域名的 A 记录
7. 等待一个检测周期后复检全部已分配 IP；仅为异常 IP 对应的域名重新优选并更新 DNS，正常 IP 保持不变，缺失分配单独补齐；启用补充检查时，等待期间每 5 分钟验证一次当前最优 IP

## 快速开始

### Docker Compose（推荐）

```bash
cp .env.example .env
docker compose up -d
```

启动后访问 `http://<主机IP>:19401/`，首次启动会自动进入「设置」页面：填写 DNS 服务商凭证、添加域名，再在「更新排名」中把域名放到需要的名次，保存后即开始优选。**请同时在设置页面中设置登录密码**。

默认拉取 `ghcr.io/aiqinxuancai/cloudflarefastcdn:latest`。可在 `.env` 中修改 `IMAGE_TAG` 固定版本；如果 GHCR 软件包为私有，请先执行 `docker login ghcr.io`。

### Docker

```bash
docker run -d \
  --name cloudflare-fast-cdn \
  --restart unless-stopped \
  --cap-add NET_RAW \
  -e TZ=Asia/Shanghai \
  -p 19401:19401 \
  -v cloudflare-fast-cdn-data:/data \
  ghcr.io/aiqinxuancai/cloudflarefastcdn:latest
```

需要更换访问端口时，修改 `-p` 冒号左侧的宿主机端口即可，无需改动容器内的监听端口。

### 直接运行

```bash
CloudflareFastCDN
```

首次运行会在当前目录生成 `settings.json`。管理页面默认只监听 `127.0.0.1`，需要从其他设备访问时，把配置文件中的 `web.host` 改为 `0.0.0.0` 后重启。

## Web 管理页面

程序内置 Web 管理页面，默认监听 `19401` 端口：

- **概览**：当前运行阶段、下次复检倒计时、已分配 IP 及其基线/最近延迟
- **折线图**：已分配 IP 的 HTTP 延迟趋势和每轮优选的 Top1 延迟，支持 6 小时 / 24 小时 / 7 天 / 30 天 / 全部时间范围
- **优选详情**：正在进行或最近一次优选的候选 IP 验证结果、DNS 更新结果，以及历史优选记录
- **运行日志**：实时查看，支持关键字筛选
- **立即优选**：立即执行一次全量优选并更新 DNS；当前处于复检或预检时会排队，在其完成后执行
- **立即复检**：提前结束等待，复检全部已分配 IP，仅更换异常 IP 并补齐缺失分配，保留正常 IP
- **设置**：修改配置文件中的全部项目，包括各服务商的域名和更新排名

设置保存后立即写入配置文件并生效：周期、超时等参数从下一次检测开始使用；修改已排名的域名、名次或对应服务商的凭证会自动安排一次全量优选，只添加未排名的域名不会触发优选；标注「重启后生效」的项目需重启程序。密钥类设置只写不读，页面上仅显示前几位。

延迟历史与优选记录保存在 `web_history.json`（Docker 下为 `/data/web_history.json`），重启后保留。

设置登录密码后访问需要 HTTP Basic 认证，**暴露到局域网或公网时务必设置密码**；未设置密码时任何能访问该端口的人都可以查看和修改设置。

## 域名与更新排名

域名属于各自的 DNS 服务商：在服务商的设置中单独添加域名，添加时不需要指定排名。

排名在设置页面的「更新排名」中统一调整：拖动域名到 Top1 ~ Top8 的任意名次（不超过每轮候选 IP 数）（也可以使用每个名次下方的列表），任意服务商的域名都可以放到任意名次，同一名次可以放多个域名。每轮优选后，TopN 中的域名会被更新为第 N 名的 IP；未排名的域名会保留在配置中，但不会被更新。

只有存在已排名域名的服务商才需要填写凭证。

## 配置文件

全部设置保存在本地配置文件中，程序不再读取环境变量或命令行参数：

- 直接运行：当前目录下的 `settings.json`（`web_history.json`、`subnet_cache.json` 等数据文件同样保存在当前目录）
- Docker：数据卷中的 `/data/settings.json`

配置文件不存在时会自动生成，可以通过 Web 页面修改，也可以直接编辑后重启程序（支持 `//` 注释和尾逗号）。读取时会校验每一项，格式错误或超出范围的值会在日志中提示并使用默认值；整个文件不是有效 JSON 时会备份为 `settings.json.invalid` 并重新生成。文件包含 DNS 凭证和登录密码，在 Linux 上以仅所有者可读写（`600`）的权限保存。

```jsonc
{
  "version": 2,
  "dns": {
    "cloudflare": {
      "apiToken": "your-api-token",
      "domains": [
        { "name": "cdn.example.com", "rank": 1 },
        { "name": "cdn2.example.com", "rank": 2 },
        { "name": "backup.example.com" }   // 未排名，不会被更新
      ]
    },
    "tencentCloud": { "secretId": "", "secretKey": "", "domains": [] },
    "alibabaCloud": { "accessKeyId": "", "accessKeySecret": "", "domains": [] }
  },
  "selection": {
    "maxIps": 400,
    "httpCandidateCount": 10,
    "cachedCandidateCount": 3,
    "pingThreads": 8,
    "pingIntervalMs": 150,
    "bandwidthPriority": false,
    "updateIpList": false
  },
  "http": {
    "probeUrl": "https://www.visa.cn/",
    "probeHeaders": { "X-CDN-Probe-Token": "your_random_token" },
    "probeTimeoutMs": 4000,
    "speedTestTimeoutMs": 10000,
    "speedTestIdleTimeoutMs": 3000
  },
  "schedule": { "runMinutes": 60, "supplementalHttpCheck": false, "forceInitialFullSelection": false },
  "web": { "port": 19401, "host": "", "username": "admin", "password": "" }
}
```

### 从旧版本升级

旧版本通过环境变量（`.env`）、命令行参数（`--KEY=VALUE`）以及只有 `values` 字典的旧版 `settings.json` 配置。升级后首次启动时，如果还没有新版配置文件，程序会把这些旧配置**一次性迁移**到配置文件中，取值优先级与旧版的生效规则一致：**环境变量 > 命令行参数 > 旧版 settings.json**。

- 旧版的 `*_DOMAINS` / `*_DOMAINS2` / `*_DOMAINS3`（以及 `DOMAINS` 等兼容变量）分别迁移为对应服务商下排名为 Top1 / Top2 / Top3 的域名
- 旧版 `settings.json` 会先备份为 `settings.json.v1.bak`
- 迁移完成后环境变量和命令行参数不再生效；启动日志会列出仍然残留的旧变量名称，确认迁移结果后可以把它们从 `.env` 或启动命令中删除

### 配置项

| 配置项 | 默认值 | 说明 |
| --- | --- | --- |
| `dns.cloudflare.apiToken` | 空 | Cloudflare API Token（需 DNS 编辑权限） |
| `dns.tencentCloud.secretId` / `secretKey` | 空 | 腾讯云 API 凭证 |
| `dns.alibabaCloud.accessKeyId` / `accessKeySecret` | 空 | 阿里云 API 凭证 |
| `dns.*.domains` | 空 | 该服务商的域名列表；`rank` 为 1 ~ 8 表示使用第几名的 IP 更新，省略表示未排名 |
| `selection.maxIps` | `400` | 每轮最多抽样 IP 数 |
| `selection.httpCandidateCount` | `10` | 每轮选出进入最终 HTTP 验证的 IP 数（ICMP 筛选结果与子网缓存合计，1 ~ 30），不能小于更新排名中使用的最高名次；每个候选约需 1 分钟验证 |
| `selection.cachedCandidateCount` | `3` | 每轮从子网缓存中取出验证的 IP 数（0 ~ 10），不能大于 `httpCandidateCount`；`0` 表示不从缓存取 IP |
| `selection.pingThreads` | `8` | Ping 并发线程数 |
| `selection.pingIntervalMs` | `150` | 单次 Ping 间隔（毫秒） |
| `selection.bandwidthPriority` | `false` | `true` 时启用带宽优选（下载 `/speedtest` 测速），失败自动回退延迟优选 |
| `selection.updateIpList` | `false` | 启动时更新 Cloudflare 官方 IPv4 网段列表并保存到数据目录的 `ip.txt`（重启后生效） |
| `http.probeUrl` | `https://www.visa.cn/` | HTTP 验证地址，建议使用自己的站点，必须开CF代理（黄云） |
| `http.probeHeaders` | 空 | HTTP 验证和测速请求附加的请求头，页面中每行一个 `Header-Name: Value` |
| `http.probeTimeoutMs` | `4000` | HTTP 验证超时（毫秒） |
| `http.speedTestTimeoutMs` | `10000` | 测速总超时（毫秒） |
| `http.speedTestIdleTimeoutMs` | `3000` | 测速读取空闲超时（毫秒） |
| `schedule.runMinutes` | `60` | 每轮完成后等待分钟数 |
| `schedule.supplementalHttpCheck` | `false` | 等待期间每 5 分钟补充验证当前最优 IP，连续 3 次失败则提前优选，仅更换该异常 IP 对应的域名分配 |
| `schedule.forceInitialFullSelection` | `false` | `true` 时跳过首次域名 IP 预检并强制执行全量优选（重启后生效） |
| `web.port` | `19401` | Web 管理页面端口，`0` 表示关闭；关闭后只能直接编辑配置文件，且缺少有效 DNS 配置时程序直接退出（重启后生效） |
| `web.host` | 空（自动） | 监听地址；留空时 Docker 下为 `0.0.0.0`，直接运行时为 `127.0.0.1`（重启后生效） |
| `web.username` | `admin` | Web 管理页面登录用户名 |
| `web.password` | 空 | Web 管理页面登录密码，留空则无需登录 |

`TZ` 是容器的系统时区，仍通过 Docker 环境变量设置（例如 `Asia/Shanghai`），用于决定日志时间。所有控制台日志都会携带标准时间戳，格式为 `yyyy-MM-dd HH:mm:ss.fff zzz`。

HTTP 验证使用浏览器导航风格的 `GET` 请求，优先协商 HTTP/2 并在需要时回退到 HTTP/1.1。验证只读取响应头，不下载完整页面；收到 `HTTP 403 Forbidden (GET)` 或请求超时时会立即跳过当前节点，不再重试或触发退避等待。

第二轮 ICMP 检查会选取本轮实际抽样 IP 数量的前 25%（向上取整），例如抽样 400 个时取 100 个，抽样 150 个时取 38 个。进入最终 HTTP 验证的候选共 `selection.httpCandidateCount` 个：其中 `selection.cachedCandidateCount` 个名额留给子网缓存（从延迟最低的缓存子网中各随机取一个 IP，通过 TCP Ping 即入选），其余取第二轮 ICMP 的靠前结果；缓存通过的 IP 不足时，空出的名额继续由 ICMP 结果补足。通过验证的 IP 数量少于最高名次时，缺少 IP 的名次会保留原分配并在下一周期补齐。

Cloudflare IPv4 网段列表优先读取数据目录中的 `ip.txt`（启用「启动时更新 IP 段」时下载的列表，也可以放入自定义列表），不存在时使用随程序发布的列表，因此从任意目录启动程序都能正常抽样。

每轮等待结束后，程序会先按最终 HTTP 验证的节奏复检全部已成功分配到 DNS 的 IP，不会在发现第一个异常时提前停止。延迟失效阈值为 `max(300ms, 当前基线 × 2)`，因此 100ms 基线到 300ms、400ms 基线到 800ms 时都会将对应 IP 标记为异常；HTTP 失败、错误码或超时也会标记异常。随后仅为异常 IP 对应的域名重新优选和更新 DNS，正常 IP 的域名、名次及基线保持不变；复检通过且当前延迟低于基线时，仍会下调基线用于下一周期。缺失的域名分配单独补齐，所有分配正常且没有缺失时直接进入下一等待周期。局部优选会排除当前已分配的 IP；新 IP 不足或 DNS 更新失败时保留原分配，后续周期继续复检或补缺。手动「立即优选」、DNS 配置变更及强制首次全量优选仍按原行为执行全量优选。

默认情况下，程序首次启动会解析所有已排名域名的 A 记录，对去重后的每个 IPv4 地址做一次 HTTP 检查，并以检查延迟作为后续周期复检的初始基线。只要域名解析失败、没有 IPv4 地址或任一地址检查失败，就立即进入全量优选；全部地址正常时不会改写 DNS，直接进入等待周期。设置 `schedule.forceInitialFullSelection` 为 `true` 可恢复首次启动必定全量优选的行为。

`http.probeHeaders` 可配合 Cloudflare 自定义规则放行探测请求，例如：

```txt
(http.request.uri.path eq "/cf-health.txt"
 and any(http.request.headers["x-cdn-probe-token"][*] eq "your_random_token"))
```

### 带宽测速说明

启用 `selection.bandwidthPriority` 后，程序会访问 `http.probeUrl` 同域下的 `/speedtest` 文件进行测速。建议准备一个 **≥ 4 MB** 的静态文件，测速失败时自动回退延迟优选。

## 免责声明

请仅在你拥有合法管理权限的账号和域名下使用，由此产生的任何后果由使用者自行承担。
