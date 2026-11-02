// RESET EXPLODE · testador de atraso (clique até a tela e PC até a tela)
// Placa: Arduino Leonardo, Pro Micro ou Micro (ATmega32u4: precisam de USB nativo para "ser" um mouse).
//
// Sensor de luz no A0, encostado no CENTRO da tela:
//   Fototransistor (TEPT5700 ou TEPT4400, recomendado): coletor (perna longa) no 5V, emissor no A0,
//   resistor de 10 kΩ do A0 para o GND. Fotodiodo BPW34 também serve (catodo no 5V, anodo no A0, 100 kΩ para o GND).
//   LDR comum é lento demais (dezenas de ms) e distorce o resultado.
//
// Protocolo pela serial USB (115200, uma linha por comando, resposta terminada em \n):
//   PING              -> PONG RESETLAT 1
//   LVL               -> L <média de 64 leituras do sensor, 0..1023>
//   THR <preto> <branco> -> OK            (níveis do sensor com a tela preta e branca)
//   CLICK             -> clica com o botão esquerdo e mede até a luz passar de 50% entre preto e branco:
//                        R <µs até 50%> <µs de 10% a 90%, ou -1>   ou   R TIMEOUT (500 ms sem mudar)
//   WAIT              -> espera o caractere 'F' do PC (enviado na hora em que ele apresenta o frame branco)
//                        e mede igual ao CLICK, sem clicar: atraso do PC até a tela.
#include <Mouse.h>

static int blackLvl = 0, whiteLvl = 1023;
static bool rising = true;
static char line[32];
static uint8_t len = 0;

void setup() {
  // ADC mais rápido: prescaler 16 (~13 µs por leitura, ~8 bits de precisão, de sobra para luz)
  ADCSRA = (ADCSRA & ~0x07) | 0x04;
  Serial.begin(115200);
  Mouse.begin();
}

static int readLvl() { return analogRead(A0); }

static long level(float fraction) { return blackLvl + (long)((whiteLvl - blackLvl) * fraction); }

static bool past(int v, long threshold) { return rising ? v >= threshold : v <= threshold; }

// Mede a partir de t0: tempo até a luz cruzar 50% e a subida de 10% a 90% (tempo de resposta do pixel).
static void measure(unsigned long t0) {
  long l10 = level(0.1), l50 = level(0.5), l90 = level(0.9);
  unsigned long a10 = 0, a50 = 0, a90 = 0;
  for (;;) {
    int v = readLvl();
    unsigned long now = micros();
    if (!a10 && past(v, l10)) a10 = now;
    if (!a50 && past(v, l50)) a50 = now;
    if (past(v, l90)) { a90 = now; break; }
    if (now - t0 > 500000UL) break;
  }
  if (!a50) { Serial.println(F("R TIMEOUT")); return; }
  Serial.print(F("R "));
  Serial.print(a50 - t0);
  Serial.print(' ');
  Serial.println(a10 && a90 ? (long)(a90 - a10) : -1L);
}

static void command(char *cmd) {
  if (strcmp(cmd, "PING") == 0) {
    Serial.println(F("PONG RESETLAT 1"));
  } else if (strcmp(cmd, "LVL") == 0) {
    long sum = 0;
    for (int i = 0; i < 64; i++) sum += readLvl();
    Serial.print(F("L "));
    Serial.println(sum / 64);
  } else if (strncmp(cmd, "THR ", 4) == 0) {
    int b = 0, w = 0;
    char *p = cmd + 4;
    b = atoi(p);
    p = strchr(p, ' ');
    if (p) w = atoi(p + 1);
    blackLvl = b; whiteLvl = w; rising = w > b;
    Serial.println(F("OK"));
  } else if (strcmp(cmd, "CLICK") == 0) {
    unsigned long t0 = micros();
    Mouse.press(MOUSE_LEFT);
    measure(t0);
    delay(20);
    Mouse.release(MOUSE_LEFT);
  } else if (strcmp(cmd, "WAIT") == 0) {
    unsigned long start = millis();
    while (!Serial.available()) {
      if (millis() - start > 2000) { Serial.println(F("R TIMEOUT")); return; }
    }
    unsigned long t0 = micros();
    Serial.read(); // o 'F'
    measure(t0);
  } else {
    Serial.println(F("ERR"));
  }
}

void loop() {
  while (Serial.available()) {
    char c = Serial.read();
    if (c == '\n' || c == '\r') {
      if (len > 0) { line[len] = 0; command(line); len = 0; }
    } else if (len < sizeof(line) - 1) {
      line[len++] = c;
    }
  }
}
