import cv2
import socketio
import time
import logging
import keyboard
import os
import threading

logging.basicConfig(level=logging.INFO, format='%(asctime)s - %(message)s')

SERVER_URL = "http://192.168.1.98:6590"
MAX_SIZE = 65000
FRAME_RATE = 2

sio = socketio.Client()

cap = cv2.VideoCapture(0)
cap.set(cv2.CAP_PROP_FRAME_WIDTH, 640)
cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 480)
cap.set(cv2.CAP_PROP_FPS, 30)

image_folder = r"E:\FaceDetectionSolution - Copy (2)\FaceApp02\Image"
face_images = {
    '1': os.path.join(image_folder, "1.jpg"),
    '2': os.path.join(image_folder, "2.jpg"),
    '3': os.path.join(image_folder, "dungg.jpg"),
}

is_transmitting = False
stop_transmitting = threading.Event()

@sio.event
def connect():
    logging.info("Đã kết nối tới server")

@sio.event
def disconnect():
    reset_state()

@sio.on('result')
def on_result(data):
    logging.info(f"Kết quả từ App2: {data}")
    if "Xác thực thành công" in data or "Xác thực thất bại" in data:
        reset_state()

def reset_state():
    global is_transmitting
    stop_transmitting.set()  # Dừng thread gửi webcam
    is_transmitting = False
    logging.info("Reset trạng thái, dừng gửi frame, sẵn sàng chu kỳ mới")
    time.sleep(1)  # Delay để App2 xử lý xong

def send_webcam_frames(start_time):
    last_time = time.time() - 1.0 / FRAME_RATE
    while not stop_transmitting.is_set():
        current_time = time.time()
        if current_time - start_time > 60:
            logging.info("Timeout 60 giây")
            reset_state()
            break
        if current_time - last_time >= 1.0 / FRAME_RATE:
            ret, frame = cap.read()
            if not ret:
                continue
            _, buffer = cv2.imencode('.jpg', frame, [int(cv2.IMWRITE_JPEG_QUALITY), 10])
            frame_bytes = buffer.tobytes()
            if len(frame_bytes) <= MAX_SIZE:
                sio.emit('frame', frame_bytes)
            last_time = current_time
        time.sleep(0.01)

sio.connect(SERVER_URL)

while True:
    for key, image_path in face_images.items():
        if keyboard.is_pressed(key) and not is_transmitting:
            reset_state()
            logging.info(f"Phím {key} pressed, bắt đầu chu kỳ mới")
            static_frame = cv2.imread(image_path)
            if static_frame is None:
                continue
            static_frame = cv2.resize(static_frame, (640, 480))
            _, buffer = cv2.imencode('.jpg', static_frame, [int(cv2.IMWRITE_JPEG_QUALITY), 30])
            static_bytes = buffer.tobytes()
            if len(static_bytes) > MAX_SIZE:
                continue

            sio.emit('frame', b"REF_")
            logging.info("Gửi header REF_")
            time.sleep(0.5)  # Delay để App2 nhận header
            sio.emit('frame', static_bytes)
            logging.info(f"Gửi ảnh tham chiếu, kích thước: {len(static_bytes)} bytes")
            time.sleep(0.5)  # Delay trước khi gửi webcam

            is_transmitting = True
            stop_transmitting.clear()
            start_time = time.time()
            threading.Thread(target=send_webcam_frames, args=(start_time,), daemon=True).start()
            break
    time.sleep(0.01)

cap.release()
sio.disconnect()