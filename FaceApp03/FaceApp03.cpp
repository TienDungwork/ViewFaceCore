#include <iostream>
#include "FaceDetector.h"
#include "FaceAntiSpoofing.h"
#include "FaceRecognizer.h"

using namespace seeta;

int main() {
    // Khởi tạo FaceDetector để phát hiện khuôn mặt
    FaceDetector detector("path/to/seeta_fd_frontal_v1.0.bin");

    // Khởi tạo FaceAntiSpoofing để kiểm tra giả mạo
    FaceAntiSpoofing antiSpoof("path/to/fas_first.model");

    // Khởi tạo FaceRecognizer để nhận diện khuôn mặt
    FaceRecognizer recognizer("path/to/face_model.csta");

    // Đọc ảnh đầu vào (thay bằng đường dẫn đến ảnh của bạn)
    Mat image = imread("path/to/your/image.jpg");
    if (image.empty()) {
        std::cout << "Không thể đọc ảnh!" << std::endl;
        return -1;
    }

    // Chuyển ảnh sang định dạng SeetaImageData
    SeetaImageData seetaImg;
    seetaImg.data = image.data;
    seetaImg.width = image.cols;
    seetaImg.height = image.rows;
    seetaImg.channels = image.channels();

    // Bước 1: Phát hiện khuôn mặt
    std::vector<SeetaFaceInfo> faces = detector.Detect(seetaImg);
    if (faces.empty()) {
        std::cout << "Không tìm thấy khuôn mặt!" << std::endl;
        return -1;
    }

    // Lấy khuôn mặt đầu tiên (giả sử chỉ có 1 khuôn mặt)
    SeetaFaceInfo face = faces[0];
    SeetaRect rect = face.pos;

    // Bước 2: Kiểm tra giả mạo
    int spoofResult = antiSpoof.Predict(seetaImg, rect);
    if (spoofResult == 1) { // 1 = thật, 0in 
        std::cout << "Khuôn mặt là thật!" << std::endl;

        // Bước 3: Nhận diện khuôn mặt nếu không phải giả mạo
        float features[1024]; // Kích thước đặc trưng phụ thuộc vào mô hình, thường là 1024
        recognizer.Extract(seetaImg, rect, features);

        // Ở đây bạn có thể so sánh `features` với cơ sở dữ liệu để nhận diện danh tính
        std::cout << "Đã trích xuất đặc trưng khuôn mặt!" << std::endl;
    }
    else {
        std::cout << "Phát hiện giả mạo!" << std::endl;
    }

    return 0;
}