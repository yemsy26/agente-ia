"""Dibuja sobre el recorte los párpados (parpadeo) y las bocas (hablar). Cada gesto sale como un parche PNG con su posición."""
import json
import numpy as np
import cv2

K = 8                      # se dibuja a 8× y se reduce: bordes suaves
TINTA = (52, 28, 46)       # BGR, el tono de las pestañas
ZONA_OJOS = (224, 106, 334, 144)    # x0, y0, x1, y1
ZONA_BOCA = (256, 150, 300, 180)

# Cada ojo: contorno a tapar y las dos curvas del párpado (abierto y cerrado), de la esquina de fuera a la de dentro.
OJOS = [
    {   # el de la izquierda de la imagen
        "contorno": [(233.5, 116.5), (236, 111.6), (243, 110), (252, 110), (256.5, 112.5), (259.6, 117.5), (261, 122), (260.2, 128),
                     (257, 133.5), (252, 136.8), (244, 137), (238, 135), (235, 131.5), (233.5, 126)],
        "abierto": [(232.5, 118.8), (246, 113.8), (259.6, 121.6)],
        "cerrado": [(232.0, 125.8), (246, 133.2), (260.2, 126.6)],
    },
    {   # el de la derecha
        "contorno": [(291.5, 121), (294.5, 116), (299, 112), (304, 110.2), (312, 109.8), (319, 111), (323.2, 113.8), (323.6, 121.5),
                     (322.2, 128), (319.5, 132.8), (315, 136.2), (307, 137.2), (300, 136.2), (296, 133.2), (293, 128)],
        "abierto": [(324.2, 118.6), (310, 113.4), (293.0, 121.6)],
        "cerrado": [(324.8, 125.6), (309.5, 133.2), (292.6, 126.6)],
    },
]
SONRISA = [(265.3, 157.8), (267.4, 160.0), (271, 161.6), (278.5, 162.4), (285.6, 161.3), (288.9, 159.6), (291.2, 157.6)]


def curva(p0, p1, p2, n=40):
    """Parábola que pasa por los tres puntos (p1 en t = 0.5)."""
    p0, p1, p2 = map(np.array, (p0, p1, p2))
    c = 2 * p1 - (p0 + p2) / 2
    t = np.linspace(0, 1, n)[:, None]
    return (1 - t) ** 2 * p0 + 2 * t * (1 - t) * c + t ** 2 * p2


def a_lienzo(puntos, zona):
    return np.round((np.array(puntos, np.float64) - zona[:2]) * K * 16).astype(np.int32)


def trazo(lienzo, puntos, zona, color, grueso, afina=True):
    """Línea con las puntas más finas que el centro."""
    pts = np.array(puntos, np.float64)
    n = len(pts)
    for i in range(n - 1):
        t = (i + 0.5) / (n - 1)
        ancho = grueso * (0.45 + 0.55 * np.sin(np.pi * t) ** 0.7) if afina else grueso
        a, b = a_lienzo(pts[i:i + 2], zona)
        cv2.line(lienzo, (int(a[0]), int(a[1])), (int(b[0]), int(b[1])), color, max(1, int(round(ancho * K))), cv2.LINE_AA, shift=4)


def parche(base, zona, pinta):
    """Recorta la zona, la amplía, deja que «pinta» dibuje y devuelve el parche BGRA (solo lo que cambió, con borde suave)."""
    x0, y0, x1, y1 = zona
    trozo = base[y0:y1, x0:x1, :3]
    grande = cv2.resize(trozo, None, fx=K, fy=K, interpolation=cv2.INTER_CUBIC)
    cambio = np.zeros(grande.shape[:2], np.uint8)
    pinta(grande, cambio)
    nuevo = cv2.resize(grande, (x1 - x0, y1 - y0), interpolation=cv2.INTER_AREA)
    alfa = cv2.resize(cambio, (x1 - x0, y1 - y0), interpolation=cv2.INTER_AREA).astype(np.float32) / 255
    alfa = np.clip(cv2.GaussianBlur(cv2.dilate(alfa, np.ones((3, 3), np.uint8)), (0, 0), 0.7) * 1.25, 0, 1)
    alfa *= base[y0:y1, x0:x1, 3] / 255.0
    return np.dstack([nuevo, (alfa * 255).astype(np.uint8)])


def piel_lisa(base, zona, margen=14):
    """Color de piel sin rasgos para toda la zona: plano ajustado por mínimos cuadrados a los píxeles de piel cercanos."""
    x0, y0, x1, y1 = zona
    X0, Y0, X1, Y1 = x0 - margen, y0 - margen, x1 + margen, y1 + margen
    t = base[Y0:Y1, X0:X1, :3].astype(np.float32)
    b, g, r = t[..., 0], t[..., 1], t[..., 2]
    es_piel = (r > 242) & (g > 218) & (g < 246) & (b > 200) & (b < 238) & (r - b > 14) & (r - b < 48)
    es_piel = cv2.erode(es_piel.astype(np.uint8), np.ones((3, 3), np.uint8)).astype(bool)
    ys, xs = np.nonzero(es_piel)
    A = np.column_stack([xs, ys, np.ones_like(xs)]).astype(np.float32)
    yy, xx = np.mgrid[y0 - Y0:y1 - Y0, x0 - X0:x1 - X0].astype(np.float32)
    plano = np.empty((y1 - y0, x1 - x0, 3), np.float32)
    for c in range(3):
        coef, *_ = np.linalg.lstsq(A, t[..., c][es_piel], rcond=None)
        plano[..., c] = coef[0] * xx + coef[1] * yy + coef[2]
    return np.clip(plano, 0, 255).astype(np.uint8)


def ojos(base, cierre):
    """cierre: 0 abierto … 1 cerrado."""
    x0, y0, x1, y1 = ZONA_OJOS
    piel = cv2.resize(piel_lisa(base, ZONA_OJOS), None, fx=K, fy=K, interpolation=cv2.INTER_LINEAR)

    def pinta(grande, cambio):
        for ojo_ in OJOS:
            parpado = [tuple((1 - cierre) * np.array(a) + cierre * np.array(c)) for a, c in zip(ojo_["abierto"], ojo_["cerrado"])]
            linea = curva(*parpado)
            orden = linea if linea[0][0] < linea[-1][0] else linea[::-1]
            izq, der = orden[0], orden[-1]
            # Todo lo que queda por encima del párpado, dentro del contorno del ojo, pasa a ser piel.
            ojo = np.zeros(grande.shape[:2], np.uint8)
            cv2.fillPoly(ojo, [a_lienzo(ojo_["contorno"], ZONA_OJOS)], 255, cv2.LINE_AA, shift=4)
            if cierre < 0.999:
                tapa = np.zeros_like(ojo)
                borde = [(izq[0] - 8, izq[1])] + [tuple(p) for p in orden] + [(der[0] + 8, der[1]), (der[0] + 8, y0 - 4), (izq[0] - 8, y0 - 4)]
                cv2.fillPoly(tapa, [a_lienzo(borde, ZONA_OJOS)], 255, cv2.LINE_AA, shift=4)
                ojo = cv2.min(ojo, tapa)
            ojo = cv2.GaussianBlur(ojo, (0, 0), K * 0.35)
            m = (ojo.astype(np.float32) / 255)[..., None]
            grande[:] = (piel * m + grande * (1 - m)).astype(np.uint8)
            np.maximum(cambio, ojo, out=cambio)
            # Las pestañas, sobre la línea del párpado: gruesas con el ojo abierto, finas al cerrarlo.
            capa = np.zeros(grande.shape[:2], np.uint8)
            trazo(capa, linea, ZONA_OJOS, 255, 4.6 - 1.7 * cierre)
            t = (capa.astype(np.float32) / 255)[..., None]
            grande[:] = (np.array(TINTA, np.float32) * t + grande * (1 - t)).astype(np.uint8)
            np.maximum(cambio, capa, out=cambio)

    return parche(base, ZONA_OJOS, pinta)


def boca(base, abre):
    """abre: 0 … 1."""
    piel = cv2.resize(piel_lisa(base, ZONA_BOCA, 8), None, fx=K, fy=K, interpolation=cv2.INTER_LINEAR)
    cx = 278.3
    medio = 5.6 + 2.0 * abre                       # medio ancho
    alto = 3.0 + 9.5 * abre                        # de labio a labio
    ys = 158.9 - 0.5 * abre                        # labio superior

    def pinta(grande, cambio):
        # Fuera la sonrisa dibujada.
        raya = np.zeros(grande.shape[:2], np.uint8)
        trazo(raya, SONRISA, ZONA_BOCA, 255, 5.0, afina=False)
        raya = cv2.GaussianBlur(raya, (0, 0), K * 0.5)
        m = np.clip(raya.astype(np.float32) / 255 * 1.6, 0, 1)[..., None]
        grande[:] = (piel * m + grande * (1 - m)).astype(np.uint8)
        np.maximum(cambio, (m[..., 0] * 255).astype(np.uint8), out=cambio)
        # La boca abierta: labio superior casi recto, inferior en media elipse.
        a = np.linspace(0, np.pi, 40)
        inf = np.column_stack([cx + medio * np.cos(a), ys + 0.7 + (alto - 0.7) * np.sin(a) ** 0.85])
        sup = curva((cx - medio, ys + 0.7), (cx, ys + 0.1), (cx + medio, ys + 0.7), 20)
        forma = np.vstack([inf, sup])
        hueco = np.zeros(grande.shape[:2], np.uint8)
        cv2.fillPoly(hueco, [a_lienzo(forma, ZONA_BOCA)], 255, cv2.LINE_AA, shift=4)
        dentro = np.empty_like(grande)
        dentro[:] = (84, 62, 152)                   # BGR: rojo vino
        if abre > 0.4:                              # lengua: un óvalo más claro apoyado abajo
            lengua = np.zeros_like(hueco)
            c = a_lienzo([(cx, ys + alto * 0.98)], ZONA_BOCA)[0]
            ejes = (int(medio * 0.66 * K * 16), int(alto * 0.36 * K * 16))
            cv2.ellipse(lengua, (int(c[0]), int(c[1])), ejes, 0, 0, 360, 255, -1, cv2.LINE_AA, shift=4)
            lengua = cv2.min(lengua, hueco)
            tl = (lengua.astype(np.float32) / 255)[..., None]
            dentro = (np.array((138, 132, 238), np.float32) * tl + dentro * (1 - tl)).astype(np.uint8)
        h = (hueco.astype(np.float32) / 255)[..., None]
        grande[:] = (dentro * h + grande * (1 - h)).astype(np.uint8)
        np.maximum(cambio, hueco, out=cambio)
        linea = np.zeros(grande.shape[:2], np.uint8)
        cv2.polylines(linea, [a_lienzo(forma, ZONA_BOCA)], True, 255, int(1.3 * K), cv2.LINE_AA, shift=4)
        t = (linea.astype(np.float32) / 255)[..., None]
        grande[:] = (np.array((60, 44, 108), np.float32) * t + grande * (1 - t)).astype(np.uint8)
        np.maximum(cambio, linea, out=cambio)

    return parche(base, ZONA_BOCA, pinta)


def pega(base, parche_, zona):
    x0, y0, x1, y1 = zona
    a = parche_[..., 3:4].astype(np.float32) / 255
    base[y0:y1, x0:x1, :3] = (parche_[..., :3] * a + base[y0:y1, x0:x1, :3] * (1 - a)).astype(np.uint8)


CIERRES = (0.42, 0.78, 1.0)
ABERTURAS = (0.25, 0.6, 1.0)


def genera(base, carpeta="."):
    partes = {}
    for i, cierre in enumerate(CIERRES, 1):
        cv2.imwrite(f"{carpeta}/ojos_{i}.png", ojos(base, cierre))
        partes[f"ojos_{i}"] = {"x": ZONA_OJOS[0], "y": ZONA_OJOS[1]}
    for i, abre in enumerate(ABERTURAS, 1):
        cv2.imwrite(f"{carpeta}/boca_{i}.png", boca(base, abre))
        partes[f"boca_{i}"] = {"x": ZONA_BOCA[0], "y": ZONA_BOCA[1]}
    return partes


def con_gesto(base, o, b, carpeta="."):
    cara = base.copy()
    if o: pega(cara, cv2.imread(f"{carpeta}/ojos_{o}.png", cv2.IMREAD_UNCHANGED), ZONA_OJOS)
    if b: pega(cara, cv2.imread(f"{carpeta}/boca_{b}.png", cv2.IMREAD_UNCHANGED), ZONA_BOCA)
    return cara


def sobre_gris(t):
    a = t[..., 3:4].astype(np.float32) / 255
    return (t[..., :3] * a + np.array((70, 60, 60), np.float32) * (1 - a)).astype(np.uint8)


if __name__ == "__main__":
    base = cv2.imread("cuerpo.png", cv2.IMREAD_UNCHANGED)
    json.dump(genera(base), open("partes.json", "w"), indent=1)

    # Hoja de prueba: la cara con cada combinación, ampliada y a tamaño de pantalla.
    filas = []
    for o in range(4):
        fila = [cv2.resize(sobre_gris(con_gesto(base, o, b)[86:190, 212:346]), None, fx=3, fy=3, interpolation=cv2.INTER_CUBIC) for b in range(4)]
        filas.append(np.hstack(fila))
    cv2.imwrite("vista/caras.png", np.vstack(filas))
    tiras = []
    for esc in (0.32, 0.5):
        tira = [cv2.resize(sobre_gris(con_gesto(base, o, b)[0:260, 150:410]), None, fx=esc, fy=esc, interpolation=cv2.INTER_AREA)
                for o, b in ((0, 0), (1, 0), (2, 0), (3, 0), (0, 1), (0, 2), (0, 3), (3, 3))]
        tira = np.hstack(tira)
        tiras.append(cv2.copyMakeBorder(tira, 0, 0, 0, 1100 - tira.shape[1], cv2.BORDER_CONSTANT, value=(70, 60, 60)))
    cv2.imwrite("vista/caras_real.png", np.vstack(tiras))
