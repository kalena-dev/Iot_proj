#pragma once
// Copy to secrets.h (ignored by Git). Flash once per ESP32, changing FRIDGE_TOPIC.
#define WIFI_SSID "YOUR_WIFI_SSID"
#define WIFI_PASSWORD "YOUR_WIFI_PASSWORD"
#define MQTT_HOST "192.168.1.100"  // Raspberry Pi LAN IP, NOT localhost
#define MQTT_PORT 1883
#define MQTT_USER "northstar"
#define MQTT_PASSWORD "YOUR_MQTT_PASSWORD"
#define FRIDGE_TOPIC "Frig1"      // Second ESP32 must use "Frig2" (case-sensitive)
#define DHT_PIN 4
