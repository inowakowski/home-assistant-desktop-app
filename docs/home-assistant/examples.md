# Automation examples

Starting points for automations. They use the MQTT engine's entities of a computer whose device name is *Laptop*; replace the entity IDs with your own, which you find on the device's page in Home Assistant. They are examples to adapt, not tested recipes for your home.

## Light follows the screen

A desk lamp that is on while the computer's screen is on.

```yaml
automation:
  - alias: Desk lamp follows the screen
    triggers:
      - trigger: state
        entity_id: binary_sensor.laptop_display
        to: ["on", "off"]
    actions:
      - action: light.turn_{{ trigger.to_state.state }}
        target:
          entity_id: light.desk_lamp
```

When the computer goes to sleep the sensor becomes *unavailable*, not *off*. To switch the lamp off then as well, add `unavailable` to the trigger and map it to `off`.

## Do not disturb while in a call

`microphone_in_use` is on while any app uses the microphone, which is a good sign of a call.

```yaml
automation:
  - alias: Call in progress
    triggers:
      - trigger: state
        entity_id: binary_sensor.laptop_microphone_in_use
    actions:
      - action: light.turn_{{ 'on' if trigger.to_state.state == 'on' else 'off' }}
        target:
          entity_id: light.on_air_sign
```

## Lock the computer when everyone has left

```yaml
automation:
  - alias: Lock the laptop when nobody is home
    triggers:
      - trigger: state
        entity_id: zone.home
        to: "0"
    actions:
      - action: button.press
        target:
          entity_id: button.laptop_lock_screen
```

## Pause what is playing when the doorbell rings

```yaml
automation:
  - alias: Pause media for the doorbell
    triggers:
      - trigger: state
        entity_id: binary_sensor.doorbell
        to: "on"
    conditions:
      - condition: state
        entity_id: sensor.laptop_media_playback
        state: playing
    actions:
      - action: button.press
        target:
          entity_id: button.laptop_media_play_pause
      - action: notify.send_message
        target:
          entity_id: notify.laptop_notification
        data:
          message: Someone is at the door.
```

## Tell me on the computer

Any message can be sent to the computer; see [Notifications](../features/notifications.md).

```yaml
actions:
  - action: notify.send_message
    target:
      entity_id: notify.laptop_notification
    data:
      message: The washing machine is done.
```

## Monitor power follows the docked laptop

A monitor on a smart plug that should be powered only while the laptop sits in its dock with the screen on. This needs a custom sensor for the dock; the whole example is under [Custom sensors and buttons](../features/custom-entities.md#example-is-the-laptop-docked).

## Shut the computer down at night

`shutdown` is one of the buttons that are off by default; turn it on first, see [Controls](../features/controls.md#the-power-buttons-are-off-by-default).

```yaml
automation:
  - alias: Shut the laptop down at night
    triggers:
      - trigger: time
        at: "01:00:00"
    conditions:
      - condition: state
        entity_id: binary_sensor.laptop_user_active
        state: "off"
    actions:
      - action: button.press
        target:
          entity_id: button.laptop_shut_down
```

An app with unsaved work can still hold the shutdown up, as it would when shutting down by hand.
