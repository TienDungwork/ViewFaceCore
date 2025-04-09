import socketio
import eventlet
import logging
import time
from threading import Lock

logging.basicConfig(level=logging.INFO, format='%(asctime)s - %(levelname)s - %(message)s')

sio = socketio.Server(async_mode='eventlet')
app = socketio.WSGIApp(sio)

connected_clients = {}
cycle_state = {}
cycle_lock = Lock()  # Thêm lock để đồng bộ trạng thái chu kỳ

@sio.event
def connect(sid, environ):
    logging.info(f"Client {sid} đã kết nối từ {environ.get('REMOTE_ADDR', 'unknown')}")
    with cycle_lock:
        connected_clients[sid] = {'connect_time': time.time(), 'ip': environ.get('REMOTE_ADDR', 'unknown')}
        cycle_state[sid] = {'expecting_ref': False, 'ref_received': False}
    logging.info(f"Số lượng client hiện tại: {len(connected_clients)}")

@sio.event
def disconnect(sid):
    with cycle_lock:
        if sid in connected_clients:
            del connected_clients[sid]
        if sid in cycle_state:
            del cycle_state[sid]
    logging.info(f"Client {sid} đã ngắt kết nối")
    logging.info(f"Số lượng client hiện tại: {len(connected_clients)}")

@sio.on('frame')
def handle_frame(sid, data):
    try:
        logging.info(f"Nhận frame từ {sid}, kích thước: {len(data)} bytes")
        
        with cycle_lock:
            if sid not in cycle_state:
                cycle_state[sid] = {'expecting_ref': False, 'ref_received': False}

            # Kiểm tra header REF_
            if len(data) == 4 and data == b"REF_":
                cycle_state[sid] = {'expecting_ref': True, 'ref_received': False}
                logging.info(f"Bắt đầu chu kỳ mới cho {sid} với header REF_")
                sio.emit('frame', data, skip_sid=sid)
                return
            
            # Chỉ chuyển tiếp ảnh tham chiếu đầu tiên sau header REF_
            if cycle_state[sid]['expecting_ref'] and not cycle_state[sid]['ref_received']:
                if len(data) > 100:  # Giả sử ảnh tham chiếu lớn hơn 100 bytes
                    cycle_state[sid]['ref_received'] = True
                    cycle_state[sid]['expecting_ref'] = False
                    logging.info(f"Chuyển tiếp ảnh tham chiếu từ {sid}")
                    sio.emit('frame', data, skip_sid=sid)
                else:
                    logging.info(f"Bỏ qua frame không hợp lệ sau REF_ từ {sid}")
                return
            
            # Chuyển tiếp frame webcam nếu đã nhận ảnh tham chiếu
            if cycle_state[sid]['ref_received']:
                sio.emit('frame', data, skip_sid=sid)
                logging.debug(f"Đã chuyển tiếp frame webcam từ {sid}")
            else:
                logging.info(f"Bỏ qua frame từ {sid} vì chưa nhận ảnh tham chiếu hợp lệ")

    except Exception as e:
        logging.error(f"Lỗi khi xử lý frame từ {sid}: {str(e)}")

@sio.on('result')
def handle_result(sid, data):
    try:
        logging.info(f"Nhận kết quả từ {sid}: {data}")
        with cycle_lock:
            if sid in cycle_state:
                cycle_state[sid] = {'expecting_ref': False, 'ref_received': False}
        sio.emit('result', data, skip_sid=sid)
        logging.debug(f"Đã chuyển tiếp kết quả từ {sid}")
    except Exception as e:
        logging.error(f"Lỗi khi xử lý kết quả từ {sid}: {str(e)}")

def start_server():
    SERVER_IP = '192.168.1.98'
    SERVER_PORT = 6590
    logging.info(f"Server Socket.IO đang khởi động tại {SERVER_IP}:{SERVER_PORT}")
    try:
        eventlet.wsgi.server(eventlet.listen((SERVER_IP, SERVER_PORT)), app)
    except Exception as e:
        logging.error(f"Lỗi khi khởi động server: {str(e)}")
        raise

if __name__ == '__main__':
    try:
        start_server()
    except KeyboardInterrupt:
        logging.info("Server đã dừng bởi người dùng (Ctrl+C)")
    except Exception as e:
        logging.error(f"Lỗi nghiêm trọng trong quá trình chạy server: {str(e)}")