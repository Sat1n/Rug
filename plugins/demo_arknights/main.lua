local started = false

local function emit(id, title, status)
  rug.set_state("demo.step_event",
    '{"id":"' .. id .. '","title":"' .. title .. '","status":"' .. status .. '"}')
end

local function selected(csv, id)
  return string.find("," .. csv .. ",", "," .. id .. ",", 1, true) ~= nil
end

function on_init()
  rug.log("info", "明日方舟演示脚本已加载")
end

function on_tick()
  if started then return end
  started = true
  local csv = rug.get_state("demo.selection") or ""
  if selected(csv, "wake") then
    emit("wake", "正在唤醒模拟器（模拟）", "Running")
    rug.sleep(300)
    emit("wake", "模拟器已就绪（模拟）", "Succeeded")
  end
  if selected(csv, "ocr") then
    emit("ocr", "正在 OCR 识别主页（模拟）", "Running")
    rug.sleep(300)
    emit("ocr", "已识别主页（模拟）", "Succeeded")
  end
  if selected(csv, "farm") then
    emit("farm", "正在执行刷图任务（模拟）", "Running")
    rug.sleep(300)
    emit("farm", "刷图步骤完成（模拟）", "Succeeded")
  end
  rug.set_state("demo.completed", true)
end

function on_stop()
  rug.log("info", "明日方舟演示脚本已停止")
end
