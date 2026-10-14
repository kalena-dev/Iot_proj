#include <WiFi.h>
#include <PubSubClient.h>
#include <DHT.h>
#include "secrets.h"

// Both ESP32 boards run this sketch; only FRIDGE_TOPIC differs.
WiFiClient network;
PubSubClient mqtt(network);
DHT dht(DHT_PIN, DHT11);
const unsigned long SAMPLE_INTERVAL_MS = 5000; // DHT11 requires >= 2 seconds.
unsigned long lastSample = 0;
unsigned long lastMqttAttempt = 0;
unsigned long lastWifiAttempt = 0;
uint32_t sequence = 0;
uint32_t bootId;
char clientId[64];

void setup() {
  Serial.begin(115200);
  dht.begin();
  bootId = esp_random();
  snprintf(clientId, sizeof(clientId), "northstar-%s-%08lx", FRIDGE_TOPIC, (unsigned long)bootId);
  WiFi.mode(WIFI_STA);
  WiFi.setAutoReconnect(true);
  WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
  mqtt.setServer(MQTT_HOST, MQTT_PORT);
  mqtt.setBufferSize(256);
  mqtt.setKeepAlive(20);
  mqtt.setSocketTimeout(3);
  Serial.printf("Monitoring %s on DHT GPIO %d\n", FRIDGE_TOPIC, DHT_PIN);
}

void loop() {
  const unsigned long now = millis();
  if (WiFi.status() != WL_CONNECTED) {
    if (now - lastWifiAttempt >= 10000) {
      lastWifiAttempt = now;
      WiFi.reconnect();
      Serial.println("WiFi unavailable; reconnecting.");
    }
    delay(10);
    return;
  }
  if (!mqtt.connected() && now - lastMqttAttempt >= 3000) {
    lastMqttAttempt = now;
    bool connected = strlen(MQTT_USER) > 0
      ? mqtt.connect(clientId, MQTT_USER, MQTT_PASSWORD)
      : mqtt.connect(clientId);
    Serial.println(connected ? "MQTT connected." : "MQTT unavailable; will retry.");
  }
  mqtt.loop();
  if (mqtt.connected() && now - lastSample >= SAMPLE_INTERVAL_MS) {
    lastSample = now;
    const float humidity = dht.readHumidity();
    const float temperature = dht.readTemperature();
    if (isnan(humidity) || isnan(temperature) || temperature < 0 || temperature > 50 || humidity < 0 || humidity > 100) {
      Serial.println("DHT11 read failed; no invalid/stale measurement published.");
    } else {
      char payload[160];
      snprintf(payload, sizeof(payload),
        "{\"temperature\":%.1f,\"humidity\":%.1f,\"sampleId\":\"%08lx-%lu\"}",
        temperature, humidity, (unsigned long)bootId, (unsigned long)sequence++);
      // No retained readings. PubSubClient publishes at QoS 0; next sample is 5s later.
      if (mqtt.publish(FRIDGE_TOPIC, payload, false)) Serial.printf("%s %s\n", FRIDGE_TOPIC, payload);
      else Serial.println("Publish failed; waiting for the next fresh sample.");
    }
  }
  delay(10);
}
