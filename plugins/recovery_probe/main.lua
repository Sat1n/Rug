function on_init()
  local retained = 42
  rug.sleep(5000)
  rug.press_key(66, 80)
  rug.set_state('steps.before', 1)
  local result = rug.agent.resolve_anomaly('controlled-ready', 'red panel must become green')
  assert(type(result) == 'table' and result.status == 'resolved' and retained == 42)
  assert(rug.get_state('steps.before') == 1)
  rug.press_key(66, 80)
  rug.set_state('steps.after', 1)
end
function on_tick() rug.sleep(60000) end
function on_stop() rug.log('[recovery-probe] on_stop') end
