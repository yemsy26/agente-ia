"""Separa el antebrazo que saluda: una capa con el antebrazo y la mano, y el cuerpo sin ellos (la manga acaba en el codo)."""
import numpy as np
import cv2
from scipy import ndimage as ndi

# Contorno aproximado del brazo entero (por fuera de su línea), en coordenadas del recorte.
BRAZO = [(364, 222), (373, 221), (385, 239), (396, 264), (403, 272), (424, 318), (421, 329), (429, 344), (441, 359), (456, 377),
         (471, 394), (489, 411), (496, 423), (497, 441), (493, 458), (499, 469), (498, 484), (505, 487), (524, 500), (553, 514),
         (558, 528), (550, 546), (486, 538), (474, 508), (467, 495), (449, 496), (442, 480), (428, 473), (410, 458), (398, 433),
         (390, 412), (383, 396), (377, 375), (374, 350), (370, 320), (367, 290), (364, 260), (361, 238)]
SEMILLAS = [(420, 400), (450, 374), (470, 480), (500, 505), (405, 300), (385, 300), (430, 440), (380, 250), (468, 400)]
CODO = (421.0, 392.0)      # alrededor de él gira el antebrazo
EJE = (0.400, 0.917)       # dirección del brazo, del hombro a la mano
RADIO = 37.0               # medio ancho de la manga en el codo
R0, R1 = 26.0, 62.0        # hasta R0 del codo no se mueve; de R1 en adelante gira entero (entre medias, la tela se dobla)
TINTA = (96, 44, 40)       # BGR: la línea de contorno de la chaqueta


def brazo_entero(base):
    h, w = base.shape[:2]
    bgr = base[..., :3].astype(np.float32)
    lum = bgr.mean(2)
    alfa = base[..., 3] > 0
    poli = np.zeros((h, w), np.uint8)
    cv2.fillPoly(poli, [np.array(BRAZO, np.int32)], 255)
    b, g, r = bgr[..., 0], bgr[..., 1], bgr[..., 2]
    pelo = (b - r > 30) & (r > 120) & (b > 200)             # lavanda: el pelo que asoma junto a la manga
    dentro = (poli > 0) & alfa & (lum > 118)
    etiquetas, _ = ndi.label(dentro)
    crecido = np.zeros((h, w), bool)
    for x, y in SEMILLAS:
        if etiquetas[y, x]:
            crecido |= etiquetas == etiquetas[y, x]
    crecido = cv2.dilate(crecido.astype(np.uint8), np.ones((3, 3), np.uint8), iterations=3).astype(bool)
    brazo = crecido | ((poli > 0) & ~pelo)
    brazo = ndi.binary_fill_holes(brazo)
    brazo &= cv2.dilate(poli, np.ones((3, 3), np.uint8), iterations=2) > 0
    return brazo & alfa


def capas(base):
    h, w = base.shape[:2]
    brazo = brazo_entero(base)
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
    dx, dy = xx - CODO[0], yy - CODO[1]
    lado = dx * EJE[0] + dy * EJE[1]                         # > 0: hacia la mano
    r = np.hypot(dx, dy)

    # Capa del antebrazo: del codo a la punta de los dedos, más un solape hacia el hombro para que no se vea la unión.
    ante = brazo & ((lado > -18) | (r < RADIO))
    suave = cv2.GaussianBlur(ante.astype(np.float32), (0, 0), 0.6)
    capa = base.copy()
    capa[..., 3] = (base[..., 3] * np.clip(suave * 1.6, 0, 1)).astype(np.uint8)
    capa[~cv2.dilate(ante.astype(np.uint8), np.ones((3, 3), np.uint8)).astype(bool)] = 0
    # El borde del solape se difumina hacia el hombro: sobre la manga del cuerpo no se nota dónde empieza.
    funde = np.clip((lado + 18) / 10, 0, 1)
    funde = np.maximum(funde, (r < RADIO - 6).astype(np.float32))
    capa[..., 3] = (capa[..., 3] * funde).astype(np.uint8)

    # El cuerpo sin antebrazo: la manga termina en un codo redondo, con su línea.
    fuera = brazo & (lado > 0) & (r > RADIO)
    fuera = cv2.dilate(fuera.astype(np.uint8), np.ones((3, 3), np.uint8), iterations=2).astype(bool) & (lado > 0) & (r > RADIO - 0.5)
    cuerpo = base.copy()
    cuerpo[fuera] = 0
    K = 4
    arco = np.zeros((h * K, w * K), np.uint8)
    ang = np.degrees(np.arctan2(EJE[1], EJE[0]))
    cv2.ellipse(arco, (int(CODO[0] * K * 16), int(CODO[1] * K * 16)), (int((RADIO - 1.2) * K * 16),) * 2, 0, ang - 86, ang + 86,
                255, int(2.4 * K), cv2.LINE_AA, shift=4)
    arco = cv2.resize(arco, (w, h), interpolation=cv2.INTER_AREA).astype(np.float32) / 255
    arco *= cv2.erode(brazo.astype(np.uint8), np.ones((3, 3), np.uint8)) * (cuerpo[..., 3] > 0)
    cuerpo[..., :3] = (np.array(TINTA, np.float32) * arco[..., None] + cuerpo[..., :3] * (1 - arco[..., None])).astype(np.uint8)

    ys, xs = np.nonzero(capa[..., 3])
    x0, y0, x1, y1 = xs.min() - 2, ys.min() - 2, min(w, xs.max() + 3), min(h, ys.max() + 3)
    return capa[y0:y1, x0:x1], (int(x0), int(y0)), cuerpo


def dobla(cuerpo, capa, origen, grados, r0=R0, r1=R1):
    """Vista previa: el antebrazo girado alrededor del codo, igual que lo hará el programa."""
    h, w = cuerpo.shape[:2]
    lienzo = np.zeros((h, w, 4), np.uint8)
    lienzo[origen[1]:origen[1] + capa.shape[0], origen[0]:origen[0] + capa.shape[1]] = capa
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
    dx, dy = xx - CODO[0], yy - CODO[1]
    r = np.hypot(dx, dy)
    t = np.clip((r - r0) / max(1e-3, r1 - r0), 0, 1)
    ang = np.radians(grados) * t * t * (3 - 2 * t)           # subir = girar contra el reloj en pantalla
    c, s = np.cos(ang), np.sin(ang)
    mapx = (CODO[0] + dx * c - dy * s).astype(np.float32)
    mapy = (CODO[1] + dx * s + dy * c).astype(np.float32)
    pre = lienzo.astype(np.float32)
    pre[..., :3] *= pre[..., 3:4] / 255
    girado = cv2.remap(pre, mapx, mapy, cv2.INTER_LINEAR, borderMode=cv2.BORDER_CONSTANT, borderValue=0)
    a = girado[..., 3:4] / 255
    fondo = cuerpo.astype(np.float32)
    fondo[..., :3] *= fondo[..., 3:4] / 255
    sale = girado + fondo * (1 - a)
    alfa = np.maximum(sale[..., 3:4], 1e-3)
    return np.dstack([sale[..., :3] / alfa * 255, sale[..., 3]]).clip(0, 255).astype(np.uint8)


def gris(t, color=(70, 60, 60)):
    a = t[..., 3:4].astype(np.float32) / 255
    return (t[..., :3] * a + np.array(color, np.float32) * (1 - a)).astype(np.uint8)


if __name__ == "__main__":
    base = cv2.imread("cuerpo.png", cv2.IMREAD_UNCHANGED)
    capa, origen, cuerpo = capas(base)
    cv2.imwrite("brazo.png", capa)
    cv2.imwrite("sin_brazo.png", cuerpo)
    print("antebrazo", capa.shape, "en", origen)
    solo = np.zeros_like(base)
    solo[origen[1]:origen[1] + capa.shape[0], origen[0]:origen[0] + capa.shape[1]] = capa
    cv2.imwrite("vista/brazo.png", np.hstack([gris(v)[190:560, 330:558] for v in (cuerpo, solo, dobla(cuerpo, capa, origen, 0))]))
    fila1 = [gris(dobla(cuerpo, capa, origen, g))[150:560, 300:558] for g in (40, 80, 120, 135)]
    fila2 = [gris(dobla(cuerpo, capa, origen, g, RADIO, RADIO + 0.01))[150:560, 300:558] for g in (40, 80, 120, 135)]
    cv2.imwrite("vista/saludo.png", np.vstack([np.hstack(fila1), np.hstack(fila2)]))
    diferencia = np.abs(dobla(cuerpo, capa, origen, 0).astype(int) - base.astype(int)).max()
    print("diferencia máxima con el original en reposo:", diferencia)
