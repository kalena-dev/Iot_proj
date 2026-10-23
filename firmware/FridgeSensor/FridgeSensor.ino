#include <WiFi.h>
#include <ESPmDNS.h>
#include <PubSubClient.h>
#include <DHT.h>
#include "secrets.h"

// Both ESP32 boards use GPIO26. flash-sensor.sh selects a topic at build time.
// For Arduino IDE uploads, change this default to "Frig2" for board 2.
#ifndef FRIDGE_TOPIC
#define FRIDGE_TOPIC "Frig1"
#endif
#ifndef DHT_PIN
#define DHT_PIN 26
#endif
#ifndef MQTT_HOST
#define MQTT_HOST ""  // Empty = discover the broker; no embedded server IP.
#endif

WiFiClient network;
PubSubClient mqtt(network);
DHT dht(DHT_PIN, DHT11);
const unsigned long SAMPLE_INTERVAL_MS = 5000; // DHT11 requires >= 2 seconds.
const unsigned long DISCOVERY_INTERVAL_MS = 10000;
unsigned long lastSample = 0;
unsigned long lastMqttAttempt = 0;
unsigned long lastWifiAttempt = 0;
unsigned long lastDiscovery = 0;
uint32_t sequence = 0;
uint32_t bootId;
char clientId[64];
bool wifiWasConnected = false;
bool mdnsStarted = false;
bool brokerResolved = false;

bool resolveBroker() {
  IPAddress address;
  uint16_t port = MQTT_PORT;
  if (strlen(MQTT_HOST) > 0) {
    // Optional stable DNS hostname for networks that filter multicast.
    String host = MQTT_HOST;
    if (host.endsWith(".local")) {
      if (!mdnsStarted) mdnsStarted = MDNS.begin(clientId);
      host.remove(host.length() - 6);
      if (mdnsStarted) address = MDNS.queryHost(host);
    } else if (!address.fromString(MQTT_HOST)) {
      WiFi.hostByName(MQTT_HOST, address);
    }
  } else {
    if (!mdnsStarted) mdnsStarted = MDNS.begin(clientId);
    if (mdnsStarted) {
      const int count = MDNS.queryService("northstar-mqtt", "tcp");
      for (int i = 0; i < count; ++i) {
        if (MDNS.txt(i, "version") != "1") continue;
        const IPAddress candidate = MDNS.address(i);
        if (candidate == IPAddress(0, 0, 0, 0) || MDNS.port(i) == 0) continue;
        address = candidate;
        port = MDNS.port(i);
        break;
      }
    }
  }
  if (address == IPAddress(0, 0, 0, 0)) {
    Serial.println("Broker not found. Start the IoT service; allow local mDNS UDP5353 and MQTT TCP traffic.");
    return false;
  }
  mqtt.setServer(address, port);
  Serial.printf("Broker resolved: %s:%u; topic %s\n", address.toString().c_str(), port, FRIDGE_TOPIC);
  return true;
}

void setup() {
  Serial.begin(115200);
  dht.begin();
  bootId = esp_random();
  snprintf(clientId, sizeof(clientId), "northstar-%s-%08lx", FRIDGE_TOPIC, (unsigned long)bootId);
  WiFi.mode(WIFI_STA);
  WiFi.setHostname(clientId);
  WiFi.setAutoReconnect(true);
  WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
  mqtt.setBufferSize(256);
  mqtt.setKeepAlive(20);
  mqtt.setSocketTimeout(3);
  Serial.printf("Monitoring %s on DHT GPIO %d; broker %s\n", FRIDGE_TOPIC, DHT_PIN,
    strlen(MQTT_HOST) > 0 ? MQTT_HOST : "automatic discovery");
}

void loop() {
  const unsigned long now = millis();
  if (WiFi.status() != WL_CONNECTED) {
    if (wifiWasConnected) {
      mqtt.disconnect();
      if (mdnsStarted) MDNS.end();
      mdnsStarted = false;
      brokerResolved = false;
      lastDiscovery = 0;
    }
    wifiWasConnected = false;
    if (now - lastWifiAttempt >= 10000) {
      lastWifiAttempt = now;
      WiFi.reconnect();
      Serial.println("WiFi unavailable; reconnecting.");
    }
    delay(10);
    return;
  }
  if (!wifiWasConnected) {
    wifiWasConnected = true;
    Serial.printf("WiFi connected; ESP32 IP %s, topic %s\n", WiFi.localIP().toString().c_str(), FRIDGE_TOPIC);
  }
  if (!mqtt.connected()) {
    if (!brokerResolved && (lastDiscovery == 0 || now - lastDiscovery >= DISCOVERY_INTERVAL_MS)) {
      lastDiscovery = now;
      brokerResolved = resolveBroker();
    }
    if (brokerResolved && now - lastMqttAttempt >= 3000) {
      lastMqttAttempt = now;
      const bool connected = strlen(MQTT_USER) > 0
        ? mqtt.connect(clientId, MQTT_USER, MQTT_PASSWORD) : mqtt.connect(clientId);
      if (connected) Serial.printf("MQTT connected; publishing to %s.\n", FRIDGE_TOPIC);
      else {
        Serial.printf("MQTT unavailable (state %d); resolving broker again.\n", mqtt.state());
        brokerResolved = false; // Re-discover/re-resolve after server IP changes.
      }
    }
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
