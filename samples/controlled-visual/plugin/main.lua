-- No target state, receiver counters, test answers, or hard-coded window position.
-- The 16px offset is the center of the shipped 32x32 template, not a locator.
function on_init()
  rug.set_state('business', 'not_confirmed')
  local capture = rug.capture()
  if capture == nil then rug.set_state('business', 'no_frame'); rug.set_state('done', true); return end
  local hit = rug.find_image('button.bmp', 0.99)
  if hit == nil then rug.set_state('business', 'not_located'); rug.set_state('done', true); return end
  local source = hit.observation
  assert(source.frameId == capture.observation.frameId)
  rug.set_state('source_frame', source.frameId)
  rug.set_state('source_observation', source.observationId)
  local request = string.format(
    '{"frameReference":"%s","observationId":"%s","frameId":"%s","runGeneration":%d,"x":%d,"y":%d,"coordinateSpace":"capture-image-pixels","button":0,"mode":1,"policyId":"roi-consistency-v1"}',
    source.frameReference, source.observationId, source.frameId, source.run.runGeneration, hit.x + 16, hit.y + 16)
  -- Exactly one submission. No legacy fallback, relocalization or click retry.
  local result = rug.click_observed(request)
  rug.set_state('input_status', result.status)
  rug.set_state('input_reason', result.reason)
  if result.status == 'completed' then
    -- wait_image captures anew on EVERY attempt. Only read-only confirmation polls.
    local complete = rug.wait_image('complete.bmp', 1500, 0.99, 25)
    if complete ~= nil then
      assert(complete.observation.frameId ~= source.frameId)
      rug.set_state('confirmation_frame', complete.observation.frameId)
      rug.set_state('confirmation_observation', complete.observation.observationId)
      rug.set_state('business', 'confirmed')
    else
      rug.set_state('business', 'unconfirmed_timeout')
    end
  end
  rug.set_state('done', true)
end
function on_tick() rug.sleep(60000) end
function on_stop() rug.log('[controlled-visual] stopped') end
