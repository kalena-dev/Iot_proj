#pragma once
// Copy to secrets.h (ignored by Git). Never reuse a password between roles.
// Use pi/provision-mqtt.sh output: embed only ca.crt, never ca.key/server.key.
#define WIFI_SSID "YOUR_WIFI_SSID"
#define WIFI_PASSWORD "YOUR_WIFI_PASSWORD"
#define MQTT_HOST ""  // Empty = mDNS discovery; optional location IP/DNS override.
#define MQTT_PORT 8883
// Must exactly match a DNS SAN in server.crt and IoT MQTT_SERVER_NAME.
#define MQTT_SERVER_NAME "northstar-broker.local"
#define FRIG1_MQTT_USER "sensorFrig1"
#define FRIG1_MQTT_PASSWORD "CHANGE_FRIG1_TO_A_DISTINCT_RANDOM_PASSWORD"
#define FRIG2_MQTT_USER "sensorFrig2"
#define FRIG2_MQTT_PASSWORD "CHANGE_FRIG2_TO_A_DIFFERENT_RANDOM_PASSWORD"
static const char MQTT_CA_CERT[] = R"PEM(
PASTE_THE_COMPLETE_ca.crt_PEM_HERE
)PEM";
