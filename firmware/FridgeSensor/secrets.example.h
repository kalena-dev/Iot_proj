#pragma once
// Copy to secrets.h (ignored by Git). Use the same credentials on both boards.
// Topic and GPIO26 defaults live in FridgeSensor.ino; flash-sensor.sh selects
// Frig1 or Frig2 at build time without editing this credentials file.
#define WIFI_SSID "YOUR_WIFI_SSID"
#define WIFI_PASSWORD "YOUR_WIFI_PASSWORD"
#define MQTT_HOST ""  // Auto-discover on the LAN. Optional override: a stable DNS hostname.
#define MQTT_PORT 1883
#define MQTT_USER "northstar"
#define MQTT_PASSWORD "YOUR_MQTT_PASSWORD"
