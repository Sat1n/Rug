-- Two single submissions; no retry, legacy click, test counters or scene oracle.
local function click_phase(phase)
  local capture = rug.capture()
  assert(capture ~= nil, 'capture unavailable')
  local hit = rug.find_image('button.bmp', 0.99)
  assert(hit ~= nil, 'button not found')
  local source = hit.observation
  assert(source.frameId == capture.observation.frameId)
  rug.set_state(phase .. '_source', source.frameId)
  local request = string.format(
    '{"frameReference":"%s","observationId":"%s","frameId":"%s","runGeneration":%d,"x":%d,"y":%d,"coordinateSpace":"capture-image-pixels","button":0,"mode":1,"policyId":"roi-consistency-v1"}',
    source.frameReference, source.observationId, source.frameId, source.run.runGeneration, hit.x + 16, hit.y + 16)
  local result = rug.click_observed(request)
  rug.set_state(phase .. '_input', result.status)
  assert(result.status == 'completed', result.reason)
  local confirmed = rug.wait_image('complete.bmp', 1500, 0.99, 25)
  assert(confirmed ~= nil and confirmed.observation.frameId ~= source.frameId, 'business not confirmed')
  rug.set_state(phase .. '_confirmation', confirmed.observation.frameId)
  rug.set_state(phase, 1)
end
function on_init()
  rug.set_state('init_count', (rug.get_state('init_count') or 0) + 1)
  local retained = { nonce = 'original-coroutine', phase = 1 }
  click_phase('before')
  rug.set_state('phase', 'waiting')
  local recovery = rug.agent.resolve_anomaly('controlled-visual-ready', 'BEFORE visually confirmed; repair panel and explicitly verify')
  assert(type(recovery) == 'table' and recovery.status == 'resolved')
  assert(retained.nonce == 'original-coroutine' and retained.phase == 1)
  assert(rug.get_state('before') == 1 and rug.get_state('init_count') == 1)
  retained.phase = 2
  rug.set_state('recovery_id', recovery.recoveryId)
  rug.set_state('coroutine_preserved', true)
  rug.set_state('phase', 'continued')
  -- New capture + actual matching, never rewrap the BEFORE reference.
  click_phase('after')
  rug.set_state('business', 'confirmed')
  rug.set_state('done', true)
end
function on_tick() rug.sleep(60000) end
function on_stop() rug.log('[controlled-visual-recovery] stopped') end
