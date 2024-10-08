# CloudflareFastCDN
使用CF公开的子网，每个`IP/24子网`中拿一个IP，然后进行**多轮ICMPPing**来筛选最快速和稳定的IP并通过API更新CF的A记录。

仅支持IPv4，不测IP的下载带宽，在家里的docker上跑了几天，效果良好。

## 如何使用

### 直接运行
编译后执行参数，例子：
```
CloudflareFastCDN --CLOUDFLARE_KEY=你的CFKEY --DOMAINS=cdn.xxx.com,cdn.hahaha.com --PING_THREADS=16 --MAX_IPS=400 --RUN_MINUTES=30 --UPDATE_IP_LIST=false
```

### Docker运行
```
docker run -e CLOUDFLARE_KEY=你的CLOUDFLARE_KEY -e DOMAINS=你要更新A记录的域名 -e PING_THREADS=16 -e MAX_IPS=400 -e RUN_MINUTES=30 -e UPDATE_IP_LIST=false aiqinxuancai/cloudfarefastcdn:latest
```

### 变量解释
* **CLOUDFLARE_KEY** #必填，请自行获取，必须有你要使用的域名的DNS区域编辑权限。
* **DOMAINS** #必填，使用半角逗号分割，将需要更新A记录的域名写出来，比如cdn.a.com,cdn2.a.com。
* **PING_THREADS** #ping的线程数，默认是16，如果CPU性能很高，可适当调高，**过高可能导致丢包率大幅提升**，我设置10在N100上没问题，在13900HK上可以开到200.
* **MAX_IPS** #最多选取多少个IP来进行测试，会在网段中选取IP后再从中均匀随机获取。
* **RUN_MINUTES** #运行间隔分钟，默认30分钟一次。
* **UPDATE_IP_LIST** #启动时更新CF的官方IPv4列表，默认为false


## 免责声明
本项目本质是一个批量Ping的命令行工具，没有过量请求造成网络攻击的代码逻辑，不提供任何互联网服务，未特地或有偿的提供给任何人，也没有任何侵入计算机系统、修改带有版权的软件及系统软件等行为，均为使用者自愿下载使用，造成任何后果与本项目无关。
