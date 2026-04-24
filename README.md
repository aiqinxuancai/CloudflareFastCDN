# CloudflareFastCDN

自动优选 Cloudflare CDN IP 并更新 DNS A 记录。支持 Cloudflare、腾讯云 DNSPod、阿里云解析。

## 工作原理

1. 从 Cloudflare IPv4 网段按 `/24` 子网抽样
2. 多轮 ICMP Ping 筛选低延迟候选 IP
3. HTTP 连通性验证，可选带宽测速（`BANDWIDTH_PRIORITY=true`）
4. 子网缓存（`subnet_cache.json`）加速历史优质节点复用
5. 按排名依次更新 `*_DOMAINS` / `*_DOMAINS2` / `*_DOMAINS3` 的 A 记录
6. 等待 `RUN_MINUTES` 后自动进入下一轮；启用补充检查时，等待期间每 5 分钟验证一次当前最优 IP

## 快速开始

### Docker Compose（推荐）

```yaml
services:
  cloudflare-fast-cdn:
    image: aiqinxuancai/cloudfarefastcdn:latest
    container_name: cloudflare-fast-cdn
    restart: unless-stopped
    environment:
      CLOUDFLARE_KEY: "your_cf_api_token"
      CLOUDFLARE_DOMAINS: "cdn-cf.example.com"
      TENCENTCLOUD_SECRET_ID: "your_secret_id"
      TENCENTCLOUD_SECRET_KEY: "your_secret_key"
      TENCENTCLOUD_DOMAINS: "cdn-tencent.example.com"
      ALIBABACLOUD_ACCESS_KEY_ID: "your_access_key_id"
      ALIBABACLOUD_ACCESS_KEY_SECRET: "your_access_key_secret"
      ALIBABACLOUD_DOMAINS: "cdn-aliyun.example.com"
      HTTP_PROBE_URL: "https://www.visa.cn/"
      RUN_MINUTES: "60"
    volumes:
      - cloudflare-fast-cdn-data:/data

volumes:
  cloudflare-fast-cdn-data:
```

```bash
docker compose up -d
docker compose logs -f cloudflare-fast-cdn
```

### Docker

```bash
docker run -d \
  --name cloudflare-fast-cdn \
  --restart unless-stopped \
  -e CLOUDFLARE_KEY=your_cf_api_token \
  -e CLOUDFLARE_DOMAINS=cdn-cf.example.com \
  -e HTTP_PROBE_URL=https://www.visa.cn/ \
  -e RUN_MINUTES=60 \
  -v cloudflare-fast-cdn-data:/data \
  aiqinxuancai/cloudfarefastcdn:latest
```

### 直接运行

```bash
CloudflareFastCDN \
  --CLOUDFLARE_KEY=your_cf_api_token \
  --CLOUDFLARE_DOMAINS=cdn-cf.example.com \
  --HTTP_PROBE_URL=https://www.visa.cn/ \
  --RUN_MINUTES=60
```

> 参数优先级：环境变量 > 命令行参数。多个域名用英文逗号分隔。

## 配置参数

### DNS 服务商

至少配置一组域名，只配置了域名的服务商才需要填写对应凭证。

| 参数 | 说明 |
| --- | --- |
| `CLOUDFLARE_KEY` | Cloudflare API Token（需 DNS 编辑权限） |
| `CLOUDFLARE_DOMAINS` / `DOMAINS2` / `DOMAINS3` | 第 1 / 2 / 3 名 IP 更新的域名 |
| `TENCENTCLOUD_SECRET_ID` / `SECRET_KEY` | 腾讯云 API 凭证 |
| `TENCENTCLOUD_DOMAINS` / `DOMAINS2` / `DOMAINS3` | 第 1 / 2 / 3 名 IP 更新的域名 |
| `ALIBABACLOUD_ACCESS_KEY_ID` / `ACCESS_KEY_SECRET` | 阿里云 API 凭证 |
| `ALIBABACLOUD_DOMAINS` / `DOMAINS2` / `DOMAINS3` | 第 1 / 2 / 3 名 IP 更新的域名 |

### 通用参数

| 参数 | 默认值 | 说明 |
| --- | --- | --- |
| `PING_THREADS` | `8` | Ping 并发线程数 |
| `MAX_IPS` | `400` | 每轮最多抽样 IP 数 |
| `PING_INTERVAL_MS` | `150` | 单次 Ping 间隔（毫秒） |
| `HTTP_PROBE_URL` | `https://www.visa.cn/` | HTTP 验证地址，建议使用自己的站点，必须开CF代理（黄云） |
| `HTTP_PROBE_TIMEOUT_MS` | `4000` | HTTP 验证超时（毫秒） |
| `HTTP_SPEEDTEST_TIMEOUT_MS` | `10000` | 测速总超时（毫秒） |
| `HTTP_SPEEDTEST_IDLE_TIMEOUT_MS` | `3000` | 测速读取空闲超时（毫秒） |
| `RUN_MINUTES` | `60` | 每轮完成后等待分钟数 |
| `BANDWIDTH_PRIORITY` | `false` | `true` 时启用带宽优选（下载 `/speedtest` 测速），失败自动回退延迟优选 |
| `UPDATE_IP_LIST` | `false` | 启动时更新 Cloudflare 官方 IPv4 网段列表 |
| `ENABLE_SUPPLEMENTAL_HTTP_CHECK` | `false` | 等待期间每 5 分钟补充验证当前最优 IP，连续 3 次失败则提前重新优选 |

### 带宽测速说明

启用 `BANDWIDTH_PRIORITY=true` 后，程序会访问 `HTTP_PROBE_URL` 同域下的 `/speedtest` 文件进行测速。建议准备一个 **≥ 4 MB** 的静态文件，测速失败时自动回退延迟优选。

## 免责声明

请仅在你拥有合法管理权限的账号和域名下使用，由此产生的任何后果由使用者自行承担。
