# 可选的 secret 文件（不放进这些文件也能跑起来）
#
# 常规做法是**在管理页里填 API Key**（模型管理页 / 通知管理页）：值加密存放在配置目录的 keys/ 下，
# 不进配置表、不进日志、不进可读导出、不进内容备份，页面也读不回它。解析顺序是
# 「管理页填的 → Secret 文件 → 环境变量（DAILYMUSINGS_SECRET_<名字>）」。
#
# 只有在「不想让凭据落在配置目录里、要由外部工具注入」时才用文件形式：
#
#   cp -r deploy/secrets.example deploy/secrets
#   chmod 700 deploy/secrets        # 目录本身只留给你
#   chmod 644 deploy/secrets/*      # ...但文件必须让容器读得到
#
# 然后在 compose 里挂上目录（根 compose.yaml 里那一行是写好的，取消注释即可）：
#
#   volumes:
#     - ./secrets:/run/secrets:ro
#
# 文件权限仍要注意：容器进程跑成 uid 1654（app），而这是一个绑定挂载，容器读到的就是宿主机上的权限位。
# 文件若是 600、属主是你，容器里就读不到——文件明明在、名字也对，可每次模型调用都报
# transcription.secret_missing / embedding.secret_missing。目录保持 700、文件 644 就没有这个问题。
# 目录不存在或为空都不影响启动：没有 Secret 只是对应功能报「没有密钥」，不会让实例起不来。
#
# 四个文件名是固定的，内容就是纯文本值本身（不要引号、不要注释）：
#   deepseek-api-key      DeepSeek 文章生成接口的凭据
#   openai-api-key        OpenAI 兼容语音转写接口的凭据
#   embedding-api-key     Embedding 接口的凭据（不用可以留空文件）
#   smtp-password         SMTP 的密码
#
# deploy/secrets/ 已被 .gitignore。本目录里的四个文件是占位空文件，不要原样使用。
