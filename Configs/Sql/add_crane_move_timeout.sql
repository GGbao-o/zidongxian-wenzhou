-- 天车配置表：新增运动到位超时（毫秒），默认 60000（60s）
-- 说明：表结构手工维护；已应用于 automated_line 库。
-- 幂等提示：MySQL 的 ADD COLUMN 不支持 IF NOT EXISTS，重复执行会报“列已存在”，可忽略。

ALTER TABLE `CraneConfig` ADD COLUMN `MoveTimeoutMs` INT NOT NULL DEFAULT 60000;
