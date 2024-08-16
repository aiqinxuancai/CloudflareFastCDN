# CloudflareFastCDN
用于自动使用ping检测状态较好的CFIP，并更新到CF的A记录中。

## 如何使用

### 直接运行
编译后执行参数，例子：
```
CloudflareFastCDN --CLOUDFLARE_KEY=你的CFKEY --DOMAINS=cdn.xxx.com,cdn.hahaha.com
```

### Docker运行
```
docker run -e CLOUDFLARE_KEY=你的CLOUDFLARE_KEY -e DOMAINS=你要更新A记录的域名 aiqinxuancai/cloudfarefastcdn:latest
```
* 环境变量
```
CLOUDFLARE_KEY
DOMAINS
```
