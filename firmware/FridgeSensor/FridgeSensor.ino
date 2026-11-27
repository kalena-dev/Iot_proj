#include <WiFi.h>
#include <WiFiClientSecure.h>
#include <time.h>
#include <ESPmDNS.h>
#include <PubSubClient.h>
#include <DHT.h>
#include "secrets.h"

// flash-sensor.sh selects the board identity; each board has its own broker role.
#ifndef FRIDGE_ID
#define FRIDGE_ID 1
#endif
#if FRIDGE_ID == 1
#define FRIDGE_TOPIC "Frig1"
#define SENSOR_MQTT_USER FRIG1_MQTT_USER
#define SENSOR_MQTT_PASSWORD FRIG1_MQTT_PASSWORD
#elif FRIDGE_ID == 2
#define FRIDGE_TOPIC "Frig2"
#define SENSOR_MQTT_USER FRIG2_MQTT_USER
#define SENSOR_MQTT_PASSWORD FRIG2_MQTT_PASSWORD
#else
#error "FRIDGE_ID must be 1 or 2"
#endif
#ifndef DHT_PIN
#define DHT_PIN 26
#endif
#ifndef MQTT_HOST
#define MQTT_HOST ""  // Empty = discover the broker; no embedded server IP.
#endif

WiFiClientSecure network;
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
IPAddress brokerAddress;

bool resolveBroker() {
  IPAddress address;
  uint16_t port = MQTT_PORT;
  if (strlen(MQTT_HOST) > 0) {
    // Location may be an IP or DNS alias; it never changes the TLS identity.
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
        if (MDNS.txt(i, "tls") != "1" ||
            MDNS.txt(i, "serverName") != MQTT_SERVER_NAME) continue;
        const IPAddress candidate = MDNS.address(i);
        if (candidate == IPAddress(0, 0, 0, 0) || MDNS.port(i) != MQTT_PORT) continue;
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
  brokerAddress = address;
  mqtt.setServer(address, port);
  Serial.printf("Broker location %s:%u; TLS identity %s; topic %s\n",
    address.toString().c_str(), port, MQTT_SERVER_NAME, FRIDGE_TOPIC);
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
  configTime(0, 0, "pool.ntp.org", "time.nist.gov");
  network.setCACert(MQTT_CA_CERT);
  network.setHandshakeTimeout(10);
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
  // Certificate validity requires a synchronized UTC clock; never skip validation.
  if (time(nullptr) < 1735689600) {
    delay(100);
    return;
  }
  if (!mqtt.connected()) {
    if (!brokerResolved && (lastDiscovery == 0 || now - lastDiscovery >= DISCOVERY_INTERVAL_MS)) {
      lastDiscovery = now;
      brokerResolved = resolveBroker();
    }
    if (brokerResolved && now - lastMqttAttempt >= 3000) {
      lastMqttAttempt = now;
      // This API preserves SNI and hostname validation while using a discovered
      // address. Requires Arduino ESP32 core 3.3.3 or newer.
      const bool secure = network.connect(brokerAddress, MQTT_PORT,
        MQTT_SERVER_NAME, MQTT_CA_CERT, nullptr, nullptr);
      const bool connected = secure &&
        mqtt.connect(clientId, SENSOR_MQTT_USER, SENSOR_MQTT_PASSWORD);
      if (connected) Serial.printf("MQTT connected; publishing to %s.\n", FRIDGE_TOPIC);
      else {
        Serial.printf("MQTT unavailable (state %d); resolving broker again.\n", mqtt.state());
        brokerResolved = false; // Re-discover/re-resolve after server IP changes.
        network.stop();
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
