# API Changelog — dành cho team Frontend

Mỗi lần backend thay đổi API, một mục mới được thêm **lên đầu** file này, cùng commit với thay đổi.
Swagger UI (`/swagger`, tài liệu `/openapi/v1.json`) luôn cho biết API **hiện tại** ra sao; file này cho biết
**cái gì vừa đổi** và frontend cần làm gì.

Mỗi mục chia 3 phần:

- **⚠️ Breaking** — frontend **bắt buộc** phải sửa, nếu không sẽ lỗi.
- **Mới** — endpoint hoặc trường mới, dùng khi cần.
- **Thay đổi hành vi** — đường dẫn và dữ liệu không đổi, nhưng cách hệ thống xử lý thay đổi.

Lỗi nghiệp vụ trả về `409` với envelope chuẩn, trường `rule` cho biết luật bị vi phạm; lỗi dữ liệu nhập trả về
`400` với `details[].field`.

---

## 2026-11-05 — G-19: nghĩa vụ học lại của hành động khắc phục

Sửa bug: hành động khắc phục yêu cầu học lại vẫn **đóng được nhờ lần đậu cũ**. Giờ mỗi nhân viên được nêu tên có một
**nghĩa vụ học lại** riêng, gắn với đúng khóa/SOP và đúng **lượt học (enrollment)** được giao; chỉ lượt đó đậu **sau khi
được giao** mới tính.

### ⚠️ Breaking (hành vi)

- **Nhân viên đã nghỉ / chuyển chi nhánh không còn bị bỏ qua im lặng.** Họ vẫn giữ nghĩa vụ và **chặn đóng** cho tới khi
  Quality Auditor **miễn trừ có lý do** (endpoint mới bên dưới). Trước đây hành động đóng được luôn.
- **Người đã đậu khóa từ trước** khi được giao học lại sẽ nhận **lượt học mới** trên cùng khóa (lượt cũ, quiz, thực hành,
  chứng chỉ cũ vẫn giữ làm lịch sử). Trước đây họ bị báo `ALREADY_ENROLLED` và không bao giờ học lại được.

### Mới

| Endpoint / trường | Ai dùng | Mô tả |
|---|---|---|
| `POST /corrective-actions/{id}/obligations/{employeeId}/exempt` `{ reason }` | QUALITY_AUDITOR | Miễn nghĩa vụ học lại cho một nhân viên (nghỉ, chuyển…). `reason` bắt buộc (≤500), ghi audit, không hoàn tác. Trả về hành động khắc phục. |
| `obligations[]` trong `GET /corrective-actions`, `GET /corrective-actions/{id}` và kết quả `start`/`close`/`exempt` | mọi role đọc được action | Mỗi phần tử: `{ id, employeeId, status: PENDING\|ASSIGNED\|EXEMPTED, courseId, recipeVersionId, enrollmentId, enrollmentState, assignedAt, completed, legacy, exemptReason, exemptedBy, exemptedAt }`. `completed = true` khi lượt học được giao đã đậu sau thời điểm giao, hoặc đã miễn. |
| `results[].obligationId` trong `POST /corrective-actions/{id}/assign-retraining` | QUALITY_AUDITOR | Nghĩa vụ ứng với từng nhân viên. Các field cũ (`enrollmentIds`, `alreadyEnrolled`, `results[].outcome`) giữ nguyên. |

### Thay đổi hành vi

- **`POST /corrective-actions/{id}/close`** khi `retrainingRequired = true`: mỗi nhân viên phải **đậu đúng lượt học được
  giao, sau thời điểm giao**, hoặc đã được miễn. Thiếu → `409` `RETRAINING_INCOMPLETE`, `details[]` mỗi người một dòng
  `"employee {id}: {lý do}"` với lý do `NOT_ASSIGNED` (chưa giao), `NOT_PASSED` (đã giao, chưa đậu),
  `LEGACY_PENDING` (dữ liệu cũ chờ đối soát). Action không yêu cầu học lại: không đổi.
- **`assign-retraining`**: gửi lại (hoặc gửi đồng thời) không tạo nghĩa vụ hay lượt học trùng — lượt đã gắn đang học
  được trả lại với `outcome: ALREADY_ENROLLED`. Nhân viên nghỉ/chuyển/đã miễn trả `SKIPPED` với `reasonCode`
  `INACTIVE` / `WRONG_BRANCH` / `EXEMPTED`, nghĩa vụ **vẫn mở** nếu chưa miễn.
- **Dữ liệu cũ**: hành động khắc phục **đang mở** tạo trước bản này được thêm nghĩa vụ `PENDING` với `legacy: true` —
  cần giao học lại hoặc miễn trừ rồi mới đóng. Hành động đã đóng giữ nguyên, không có gì bị tự đánh dấu hoàn thành.

### Chạy lại case G-19 đang FAIL

1. Nhân viên có enrollment PASSED trên khóa của SOP → tạo audit trượt, `retrainingRequired: true`, nêu tên nhân viên đó.
2. `POST …/close` → kỳ vọng `409 RETRAINING_INCOMPLETE`, detail `NOT_ASSIGNED`.
3. `POST …/assign-retraining` với khóa của SOP → `outcome: ASSIGNED`, `enrollmentId` **khác** enrollment cũ.
4. `POST …/close` → `409`, detail `NOT_PASSED`. Cho nhân viên học và đậu lượt mới → `close` → `200`.

---

## 2026-11-05 — Sửa báo cáo hao hụt nguyên liệu và đăng xuất trùng

### Mới

- **`GET /stock/loss-report`**: mỗi dòng có thêm `openingCountedOn` và `closingCountedOn` — ngày của hai lần kiểm kho
  thực sự được dùng để tính dòng đó. Nên hiển thị khoảng này cạnh số liệu (ví dụ "Kiểm 01/11 → 04/11").

### Thay đổi hành vi

- **Báo cáo hao hụt**: tồn đầu là lần kiểm gần nhất **trước hoặc đúng** `from`; hàng nhập và số ly bán giờ được cộng từ
  **ngày của lần kiểm đó** đến lần kiểm cuối, không còn cắt ở `from`. Trước đây hàng nhập/bán nằm giữa ngày kiểm đầu và
  `from` bị bỏ sót nên hao hụt bị sai. Nguyên liệu mà lần kiểm đầu và cuối là cùng một lần thì không có dòng.
- **Ngày nhập kho / kiểm kho** được so với "hôm nay" theo **giờ Việt Nam**: ghi ngày hôm nay sau 0h giờ VN không còn bị
  báo `400` "ngày trong tương lai".
- **Đăng xuất** (`POST /auth/logout`) bấm hai lần cùng lúc, hoặc trùng lúc đang refresh: không còn lỗi `500`, cả hai đều
  `204`.

---

## 2026-11-05 — Sửa lỗi phiên đăng nhập và đóng hành động khắc phục

Không đổi endpoint hay dữ liệu trả về.

### Thay đổi hành vi

- **Đặt lại mật khẩu** (`PUT /users/{id}` có `password`), vô hiệu hóa, đổi vai trò hoặc chi nhánh: refresh token phát hành
  **trước** đó giờ cũng bị từ chối (`POST /auth/refresh` → `401`), không chỉ access token. Frontend xử lý như đã ghi ở
  mục G-11: refresh `401` thì đăng xuất về trang đăng nhập.
- **Sau khi triển khai bản này, mọi refresh token cũ bị từ chối một lần** (chúng chưa mang phiên bản bảo mật): người
  dùng đang đăng nhập sẽ phải đăng nhập lại một lần.
- **Admin sửa người dùng** và gửi lại `status: ACTIVE` cho người đang hoạt động: người đó **không còn bị đăng xuất**.
  Chỉ khi trạng thái thật sự đổi (khóa ↔ mở) mới kết thúc phiên.
- **Đóng hành động khắc phục** (`POST /corrective-actions/{id}/close`) khi yêu cầu học lại: chỉ tính lần đậu **sau khi**
  hành động được tạo. Lần đậu từ trước khi audit trượt không còn đủ để đóng → `409` `RETRAINING_INCOMPLETE`. Lần đậu
  trên khóa đã chuyển `OUT_OF_DATE` (do công thức ra phiên bản mới) vẫn được tính.

---

## 2026-11-03 — Thông báo trong luồng mở bán món mới

Không đổi endpoint nào. Hộp thông báo (`GET /me/notifications`) có thêm 6 loại; chúng cũng được gửi qua email và
web push như các thông báo khác. Frontend có thể dựa vào `subject` để hiện biểu tượng hoặc dẫn link tới màn hình
phù hợp.

| `subject` | Ai nhận | Khi nào | Nên dẫn tới |
|---|---|---|---|
| `Training needed for a drink` | TRAINING_MANAGER, BRANCH_MANAGER của chi nhánh | Món được lên kế hoạch ở chi nhánh chưa đủ người có chứng chỉ (pilot bắt đầu, hoặc quyết định ROLLOUT) | Màn giao khóa / mở lớp |
| `Branch ready to open a drink` | RD_MANAGER | Chi nhánh vừa đủ số người có chứng chỉ (`READY`) | Màn readiness của pilot, nút mở bán |
| `Drink on sale` | BRANCH_MANAGER của chi nhánh | Món được mở bán ở chi nhánh | Màn nhập số ly bán |
| `Drink withdrawn` | BRANCH_MANAGER của chi nhánh | Món ngừng bán (thủ công, hoặc quyết định DISCONTINUE) | Danh sách món đang bán |
| `Pilot ended` | RD_MANAGER | Pilot tự kết thúc khi qua ngày cuối | Màn đánh giá pilot, nút ra quyết định |
| `Sales missing` | BRANCH_MANAGER của chi nhánh pilot | Pilot đang chạy có ngày (tới hôm qua) chưa nhập số ly; tối đa một lần mỗi ngày cho mỗi chi nhánh, liệt kê đủ các ngày còn thiếu | Màn nhập số ly bán / import POS |

---

## 2026-11-03 — PR `feat/backend-foundation` → `main`

Gồm `feat/backend-foundation` và `feat/retraining-scope-and-cycle`.

### ⚠️ Breaking

**1. `GET /courses/{id}/eligible-trainers` trả về dạng phân trang**

Trước: một mảng. Sau: `{ items, page, size, total }`. Tham số mới: `?q=` (tìm theo username/họ tên),
`?branchId=` (chi nhánh của lớp), `?page=&size=&sort=`.

```json
{
  "items": [
    { "id": 12, "fullName": "Nguyễn Văn A", "branchId": 1, "eligible": true,  "reason": null },
    { "id": 14, "fullName": "Trần Văn C",   "branchId": 2, "eligible": false, "reason": "belongs to another branch" }
  ],
  "page": 1, "size": 20, "total": 2
}
```

Khi chọn trainer cho buổi học, nên truyền `branchId` của lớp: trainer của chi nhánh khác sẽ có `eligible: false`.

**2. Duyệt / trả khóa học chuyển sang R&D Manager**

`POST /courses/{id}/approve` và `POST /courses/{id}/return` giờ chỉ dành cho **RD_MANAGER** (trước là
TRAINING_MANAGER). Training Manager gọi sẽ nhận `403`. Ẩn nút duyệt với Training Manager, hiện cho R&D Manager.
R&D Manager cũng được đọc `GET /courses/{id}` và `GET /courses/{id}/modules` để xem khóa trước khi duyệt.

**3. `POST /corrective-actions/{id}/assign-retraining` trả về kết quả từng nhân viên**

Kết quả có thêm `results[]`; mỗi phần tử là
`{ employeeId, outcome: "ASSIGNED" | "ALREADY_ENROLLED" | "SKIPPED", enrollmentId, reasonCode }`.
`reasonCode` khi bị bỏ qua: `NOT_FOUND`, `INACTIVE`, `WRONG_BRANCH`. Hiển thị từng dòng thay vì báo "thành công"
chung, vì có thể chỉ giao được cho một phần nhân viên. Khóa không gắn với SOP của lần audit → `409`
`RETRAINING_COURSE_NOT_ALLOWED`.

**4. Token bị từ chối ngay khi tài khoản thay đổi**

Khi tài khoản bị vô hiệu hóa, đổi vai trò, đổi chi nhánh hoặc đặt lại mật khẩu, mọi request bằng access token cũ
trả về **`401`** ngay lập tức (không chờ token hết hạn). Frontend cần: gặp `401` thì thử refresh một lần; refresh
cũng `401` thì đăng xuất và về trang đăng nhập.

**5. Refresh token chỉ dùng được một lần**

Mỗi lần `POST /auth/refresh` trả về refresh token mới; token cũ không dùng lại được (`401`). Nếu nhiều tab hoặc
nhiều request cùng refresh một lúc, chỉ một cái thành công. Frontend nên chỉ chạy **một** lần refresh tại một thời
điểm và cho các request khác chờ kết quả đó.

### Mới

| Endpoint | Ai dùng | Mô tả |
|---|---|---|
| `GET /roles` | mọi người đã đăng nhập | Danh sách vai trò kèm `permissions[]`, để ẩn/hiện chức năng theo quyền |
| `GET /users/lookup?ids=1,2,3` | ADMIN, TRAINER, TRAINING_MANAGER, QUALITY_AUDITOR, BRANCH_MANAGER | Đổi danh sách id đã lưu thành tên hiển thị; có cả người đã nghỉ, vẫn giới hạn theo chi nhánh |
| `POST /stock/receipts` | BRANCH_MANAGER | Ghi nhận nhập kho: `{ ingredientId, quantity, unit, receivedOn }` → `201` |
| `POST /stock/counts` | BRANCH_MANAGER | Ghi nhận kiểm kho: `{ ingredientId, quantity, unit, countedOn }` → `201` |
| `GET /stock/loss-report?branchId=&from=&to=` | BRANCH_MANAGER (chi nhánh mình), RD_MANAGER, ADMIN | Hao hụt nguyên liệu giữa hai lần kiểm kho, dòng nào vượt ngưỡng có `flagged: true` |

**Chứng chỉ có thêm 4 trường** (`GET /me/certificates`, `GET /certificates/{id}`):

```json
{
  "issuedBy": "SYSTEM",
  "enrollmentId": 345,
  "assessedById": 12,
  "assessedByName": "Nguyễn Văn A"
}
```

Gợi ý hiển thị: "Cấp tự động bởi hệ thống, xác nhận bởi trainer *Nguyễn Văn A*". `assessedBy*` là trainer đã chấm
**đậu** bài thực hành.

**Giao khóa (`POST /assignments`) có thêm `skipped[]`**: `{ userId, reason }` cho nhân viên bị bỏ qua khi giao
cả chi nhánh; `reason: "PREREQUISITE"` nghĩa là người đó chưa đậu khóa tiên quyết.

**Quyền xem chi tiết học viên mở rộng**: TRAINING_MANAGER và BRANCH_MANAGER (chi nhánh mình) được đọc
`GET /enrollments/{id}`, `/modules`, `/eligibility`, `/quiz-attempts`.

**CORS** cho phép frontend đọc các header `Content-Disposition`, `Content-Range`, `Accept-Ranges`,
`Content-Length` — lấy được tên file khi tải báo cáo và tua được video.

### Thay đổi hành vi

- **Thêm buổi học** (`POST /training-classes/{id}/sessions`, `PUT /training-sessions/{id}`): trainer thuộc chi nhánh
  khác với lớp bị từ chối, `400` ở trường `trainerId`. Trainer không có chi nhánh (dạy toàn chuỗi) vẫn được.
- **Đóng enrollment** (`POST /enrollments/{id}/close`) hoặc **hủy lớp** (`POST /training-classes/{id}/cancel`): lịch
  thi thực hành đang mở chuyển sang `CANCELLED`, học viên nhận thông báo **"Practical cancelled"**.
- **Reset enrollment** (`POST /enrollments/{id}/reset`): lịch thi thực hành cũ bị hủy; giai đoạn (`stage`) chỉ tính
  dữ liệu của chu kỳ học mới.
- **Giao khóa trực tiếp** cho một người chưa đậu khóa tiên quyết → `409`, `rule: "PREREQUISITE"` (giống khi mở lớp).
- **Đóng corrective action** khi còn nhân viên chưa đậu khóa học lại → `409` `RETRAINING_INCOMPLETE`.
- **Quy định đào tạo**: khóa tiên quyết không được tạo vòng lặp, cũng không được là chính nó → `400`.
- **Import POS** dùng lại `Idempotency-Key` với file khác → `409` `IDEMPOTENCY_KEY_MISMATCH`. Gửi lại đúng file đó
  thì nhận lại kết quả cũ, không import hai lần.
- **Commit impact analysis** hai lần cùng lúc → lần sau nhận `409` `IMPACT_ALREADY_COMMITTED`.
- **Nhắc hạn**: nhân viên đã bị vô hiệu hóa không còn nhận nhắc hạn, Training Manager không nhận cảnh báo quá hạn
  cho họ.

---

## 2026-10-20 — PR #7 (`feat/lookup-read-and-stage`), đã vào `main`

### ⚠️ Breaking

**`GET /assignments`: `status` đổi thành `stage` + `isOverdue`**

- Tham số lọc `?status=` đổi thành `?stage=` và thêm `?overdue=true|false`.
- Kết quả: trường `status` đổi thành `stage`, thêm `isOverdue` (boolean).
- `stage` không còn giá trị `OVERDUE`. Các giá trị: `ASSIGNED`, `IN_PROGRESS`, `THEORY_COMPLETED`, `QUIZ_PASSED`,
  `PRACTICAL_PENDING`, `COMPLETED`, `LOCKED`, `CLOSED`. Quá hạn giờ là cờ riêng, nên hiển thị cả hai, ví dụ
  "Đang học · Quá hạn".

### Mới

| Endpoint | Ai dùng | Mô tả |
|---|---|---|
| `GET /users/lookup?q=&role=&branchId=&includeInactive=` | ADMIN, TRAINER, TRAINING_MANAGER, QUALITY_AUDITOR, BRANCH_MANAGER | Danh bạ để chọn người (giao khóa, học lại, trainer của buổi học); giới hạn theo chi nhánh |
| `GET /courses/{id}/eligible-trainers` | TRAINER, TRAINING_MANAGER | Trainer đủ điều kiện dạy khóa (BR-34) kèm lý do nếu không đủ |
| `GET /corrective-actions/{id}/retraining-options` | QUALITY_AUDITOR | Nhân viên được nêu tên và các khóa gắn với SOP của lần audit, để chọn khi giao học lại |
