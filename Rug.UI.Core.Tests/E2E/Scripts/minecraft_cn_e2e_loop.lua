local function find_label(result, needle)
  for _, block in ipairs(result.blocks) do
    if string.find(block.text, needle, 1, true) then return block end
  end
  return nil
end

local function read_menu(region)
  local winrt = rug.ocr(region, 'zh-CN')
  local paddle = rug.ocr(region, nil, 'paddle:ppocr_v6_medium')
  local options = find_label(winrt, '选项') or find_label(paddle, '选项')
  local single = find_label(winrt, '单人游戏') or find_label(paddle, '单人游戏')
  return options, single
end

function on_init()
  rug.log('E2E:on_init')
  rug.sleep(300)
end

function on_tick()
  local frame = rug.capture()
  assert(frame and frame.width > 0 and frame.height > 0, 'WGC 未返回有效帧')
  local region = {x = 0, y = 0, width = frame.width, height = frame.height}
  local options, single = read_menu(region)
  assert(single, 'WinRT/Paddle 均未识别到单人游戏按钮')
  rug.log(options and 'E2E:menu_ocr=winrt+paddle' or 'E2E:menu_ocr=preflight_fallback')
  local hit = rug.find_image('options_template.bmp', 0.65)
  assert(hit and hit.similarity >= 0.65, 'OpenCV 未匹配到选项按钮模板')
  rug.log('E2E:template_matched')

  local x = options and math.floor(options.x + options.width / 2) or rug.get_config('options_x')
  local y = options and math.floor(options.y + options.height / 2) or rug.get_config('options_y')
  assert(x >= 0 and x < frame.width and y >= 0 and y < frame.height, 'OCR 坐标超出客户区')
  rug.click(x, y, 'left', 'sendinput')
  rug.log('E2E:clicked_options')
  local settings_visible = false
  for attempt = 1, 12 do
    rug.sleep(300)
    frame = rug.capture()
    assert(frame, '设置页面没有 WGC 帧')
    region.width, region.height = frame.width, frame.height
    local settings = rug.ocr(region, 'zh-CN')
    settings_visible = find_label(settings, '完成') ~= nil
    if not settings_visible then
      settings = rug.ocr(region, nil, 'paddle:ppocr_v6_medium')
      settings_visible = find_label(settings, '完成') ~= nil
    end
    if settings_visible then break end
  end
  assert(settings_visible, '点击后未在限定时间内观察到设置页面')
  rug.log('E2E:settings_visible')

  rug.press_key(27, 100)
  rug.log('E2E:pressed_escape')
  local menu_visible = false
  for attempt = 1, 12 do
    rug.sleep(300)
    frame = rug.capture()
    assert(frame, 'Esc 后没有 WGC 帧')
    region.width, region.height = frame.width, frame.height
    _, single = read_menu(region)
    menu_visible = single ~= nil
    if menu_visible then break end
  end
  assert(menu_visible, 'Esc 后未在限定时间内重新观察到主菜单')
  rug.log('E2E:returned_menu')

  rug.agent.resolve_anomaly('阶段二端到端预期事故', '已验证中文主菜单、OpenCV、选项点击和 Esc 返回')
end

function on_stop()
  rug.log('E2E:on_stop')
end
