-- 移除不再使用的数据库字段（v2.17）：
--   1) CraneConfig.MoveTimeoutMs —— 运动超时改由 appsettings.json 的 App:Crane 提供
--   2) DeviceConfig.SafePosition —— 现场确认从未使用
-- 说明：表结构手工维护，请在 automated_line 库执行。
-- 幂等提示：若列已不存在，DROP COLUMN 会报错，可忽略。

ALTER TABLE `CraneConfig` DROP COLUMN `MoveTimeoutMs`;
ALTER TABLE `DeviceConfig` DROP COLUMN `SafePosition`;
