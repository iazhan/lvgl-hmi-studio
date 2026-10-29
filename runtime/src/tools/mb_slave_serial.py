#!/usr/bin/env python3
"""mb_slave_serial.py — 串口版 Modbus RTU 从站模拟器（板端联调用）

与宿主 pty 版（logs/scratch/mb_slave.py）同一套帧处理，区别只是走真实串口：
板上 UART2（PE7/PE8）接 USB-TTL，宿主拿这块 TTL 当从站。

接线（板端 → TTL）：
    PE7 (UART2_TX) → RXD
    PE8 (UART2_RX) → TXD
    GND            → GND

用法：
    mb_slave_serial.py /dev/ttyUSB1 [--baud 9600] [--cdab] [--value 25.0]

默认 holding[100..101] 是 f32 25.0（ABCD 字序）；--cdab 时两字互换。
从站每收到一帧就打印一行，方便对着屏幕看是哪一步通了。
"""
import argparse
import os
import select
import struct
import sys
import termios
import time
import tty


def crc16(data):
    crc = 0xFFFF
    for b in data:
        crc ^= b
        for _ in range(8):
            crc = (crc >> 1) ^ 0xA001 if crc & 1 else crc >> 1
    return crc


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('port')
    ap.add_argument('--baud', type=int, default=9600)
    ap.add_argument('--cdab', action='store_true', help='32 位数据低字在前')
    ap.add_argument('--value', type=float, default=25.0, help='holding[100] 的初值')
    args = ap.parse_args()

    baud = {9600: termios.B9600, 19200: termios.B19200,
            38400: termios.B38400, 115200: termios.B115200}.get(args.baud)
    if baud is None:
        raise SystemExit('不支持的波特率 %d' % args.baud)

    fd = os.open(args.port, os.O_RDWR | os.O_NOCTTY)
    tty.setraw(fd)
    a = termios.tcgetattr(fd)
    a[4] = a[5] = baud
    termios.tcsetattr(fd, termios.TCSANOW, a)

    f = struct.unpack('<I', struct.pack('<f', args.value))[0]
    hi, lo = f >> 16, f & 0xFFFF
    holding = {100: lo, 101: hi} if args.cdab else {100: hi, 101: lo}
    coils = {}
    print('%s 从站就绪：%d baud，holding[100]=%.1f（%s）'
          % (args.port, args.baud, args.value, 'CDAB' if args.cdab else 'ABCD'),
          flush=True)

    def stamp():
        return time.strftime('%H:%M:%S') + ('.%03d' % (time.time() * 1000 % 1000))

    def take_frame(b):
        """从缓冲里找一帧完整且 CRC 正确的请求。

        不能按固定长度切：上位机重启、USB 抖动都会让缓冲区错位，一旦错位后续
        每一帧都会被丢掉——表现为板端间歇超时、界面数值变 ---。这里按
        「地址 + 功能码推长度 + CRC 校验」重新同步。
        """
        for i in range(len(b)):
            if not (1 <= b[i] <= 247):
                continue
            if i + 2 > len(b):
                return None
            fc = b[i + 1]
            if fc in (1, 2, 3, 4, 5, 6):
                n = 8
            elif fc == 0x10:
                if i + 7 > len(b):
                    return None
                n = 9 + b[i + 6]
            else:
                continue
            if i + n > len(b):
                return None
            f = b[i:i + n]
            if crc16(f[:-2]) == (f[-2] | (f[-1] << 8)):
                return f, i + n
        return None

    buf = b''
    while True:
        r, _, _ = select.select([fd], [], [], 1.0)
        if r:
            buf += os.read(fd, 512)

        while True:
            got = take_frame(buf)
            if got is None:
                if len(buf) > 16:          # 只留尾部一帧的量，其余是噪声
                    buf = buf[-16:]
                break
            frame, used = got
            buf = buf[used:]

            addr, fcode = frame[0], frame[1]
            if fcode in (1, 2, 3, 4):
                start = (frame[2] << 8) | frame[3]
                count = (frame[4] << 8) | frame[5]
                if fcode in (3, 4):
                    payload = b''.join(
                        struct.pack('>H', holding.get(start + i, 0))
                        for i in range(count))
                else:
                    bits = bytearray((count + 7) // 8)
                    for i in range(count):
                        if coils.get(start + i, 0):
                            bits[i // 8] |= 1 << (i % 8)
                    payload = bytes(bits)
                resp = bytes([addr, fcode, len(payload)]) + payload
                resp += struct.pack('<H', crc16(resp))
                os.write(fd, resp)
                print('[%s] 读 fc=%d start=%d count=%d'
                      % (stamp(), fcode, start, count), flush=True)
            elif fcode in (5, 6):
                reg = (frame[2] << 8) | frame[3]
                val = (frame[4] << 8) | frame[5]
                if fcode == 5:
                    coils[reg] = 1 if val == 0xFF00 else 0
                    print('[%s] 写线圈 addr=%d val=%d'
                          % (stamp(), reg, coils[reg]), flush=True)
                else:
                    holding[reg] = val
                    print('[%s] 写寄存器 addr=%d val=%d'
                          % (stamp(), reg, val), flush=True)
                os.write(fd, frame)
            elif fcode == 0x10:
                start = (frame[2] << 8) | frame[3]
                count = (frame[4] << 8) | frame[5]
                for i in range(count):
                    holding[start + i] = (frame[7 + 2 * i] << 8) | frame[8 + 2 * i]
                print('[%s] 写多寄存器 start=%d count=%d'
                      % (stamp(), start, count), flush=True)
                os.write(fd, frame[:6] + struct.pack('<H', crc16(frame[:6])))


if __name__ == '__main__':
    main()
