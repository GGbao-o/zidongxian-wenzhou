-- v2.38：设备增加 X/Y/Z 绝对坐标（原只有 PositionX）。
-- 表结构手工维护，此脚本供现网库补列；新建库直接建含列结构即可。
ALTER TABLE DeviceConfig ADD COLUMN PositionY DOUBLE NOT NULL DEFAULT 0;
ALTER TABLE DeviceConfig ADD COLUMN PositionZ DOUBLE NOT NULL DEFAULT 0;
