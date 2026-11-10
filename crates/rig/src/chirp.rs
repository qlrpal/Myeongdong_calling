use std::{
    fmt,
    io::{self, Write},
};

pub const SAMPLE_RATE_HZ: u32 = 48_000;
/// Number of SECDED codewords, independent of the schedule length.
pub const CODE_SPACE: usize = 2048;
pub const MAX_MARKERS: usize = 1704;

// Greedy ascending unused index, distance >= 8 from the preceding five words.
// Reproduced independently in tests/a0a3.rs; entries are codeword indices.
const MARKER_CODEWORDS: [u16; MAX_MARKERS] = [
    0, 15, 113, 126, 402, 413, 160, 7, 73, 118, 314, 405, 152, 167, 65, 78, 306, 317, 144, 159, 97,
    110, 258, 269, 176, 155, 197, 106, 294, 265, 20, 187, 193, 206, 290, 301, 16, 31, 225, 202,
    371, 366, 52, 13, 215, 130, 315, 330, 124, 169, 69, 151, 307, 266, 216, 60, 230, 5, 379, 322,
    179, 24, 174, 77, 279, 358, 161, 80, 138, 123, 261, 302, 205, 116, 146, 185, 256, 271, 95, 104,
    182, 165, 312, 275, 67, 76, 170, 157, 292, 311, 211, 329, 234, 156, 6, 304, 119, 321, 219, 173,
    10, 316, 112, 325, 223, 166, 9, 378, 208, 259, 239, 54, 153, 268, 120, 403, 71, 370, 158, 333,
    40, 145, 39, 339, 254, 348, 105, 128, 143, 274, 102, 285, 117, 184, 171, 270, 66, 293, 81, 409,
    183, 298, 94, 32, 195, 337, 189, 295, 92, 42, 198, 347, 181, 288, 201, 47, 84, 350, 178, 257,
    440, 11, 100, 221, 122, 390, 305, 491, 8, 213, 79, 164, 310, 313, 154, 64, 111, 149, 263, 296,
    186, 192, 75, 53, 340, 291, 190, 392, 207, 115, 68, 377, 406, 300, 137, 247, 2, 248, 319, 276,
    41, 449, 83, 134, 252, 280, 45, 353, 87, 386, 251, 444, 38, 141, 336, 194, 289, 287, 180, 46,
    88, 243, 361, 343, 133, 188, 272, 203, 242, 297, 351, 132, 278, 35, 245, 250, 281, 44, 326, 19,
    212, 235, 383, 172, 264, 82, 21, 163, 335, 228, 318, 320, 25, 435, 519, 86, 227, 204, 520, 57,
    303, 22, 231, 388, 72, 369, 61, 395, 210, 420, 512, 374, 109, 191, 283, 454, 129, 98, 365, 28,
    55, 398, 209, 323, 360, 36, 222, 175, 85, 385, 456, 34, 90, 308, 253, 391, 521, 224, 282, 550,
    349, 255, 49, 70, 136, 419, 284, 127, 240, 338, 1, 135, 412, 62, 121, 354, 466, 421, 142, 277,
    56, 363, 246, 356, 131, 477, 14, 545, 114, 484, 410, 125, 12, 3, 214, 352, 401, 249, 286, 331,
    140, 515, 162, 101, 217, 273, 30, 334, 168, 357, 418, 17, 63, 199, 200, 346, 372, 33, 299, 150,
    236, 536, 341, 103, 267, 416, 446, 524, 465, 99, 18, 108, 309, 220, 384, 327, 27, 498, 517,
    509, 96, 387, 59, 218, 359, 396, 527, 425, 48, 471, 226, 262, 380, 237, 51, 148, 74, 417, 376,
    399, 375, 528, 196, 43, 177, 344, 381, 530, 438, 450, 229, 513, 441, 91, 526, 342, 423, 544,
    244, 328, 523, 414, 355, 516, 241, 58, 332, 424, 455, 518, 533, 107, 468, 697, 345, 422, 37,
    522, 479, 532, 408, 238, 481, 50, 407, 525, 472, 430, 233, 514, 627, 541, 324, 362, 411, 642,
    562, 437, 93, 232, 451, 556, 547, 260, 373, 434, 139, 540, 578, 428, 615, 368, 147, 569, 534,
    431, 448, 382, 89, 552, 565, 415, 452, 442, 583, 584, 560, 29, 462, 393, 432, 503, 546, 539,
    596, 457, 621, 436, 426, 531, 554, 473, 604, 439, 458, 549, 595, 453, 538, 443, 400, 494, 548,
    597, 571, 397, 497, 364, 582, 564, 553, 389, 394, 499, 508, 23, 555, 4, 600, 483, 445, 599,
    559, 672, 460, 482, 665, 588, 579, 566, 427, 469, 474, 616, 537, 551, 367, 404, 467, 606, 608,
    647, 429, 490, 776, 26, 561, 580, 433, 461, 587, 478, 567, 568, 721, 480, 770, 463, 630, 575,
    585, 464, 664, 487, 750, 563, 577, 778, 504, 470, 660, 607, 557, 736, 505, 476, 592, 574, 719,
    641, 803, 486, 489, 542, 620, 529, 611, 502, 447, 732, 680, 723, 623, 459, 485, 646, 570, 649,
    613, 507, 500, 590, 674, 703, 705, 777, 501, 506, 598, 543, 675, 648, 813, 496, 851, 581, 511,
    618, 654, 492, 656, 633, 787, 739, 782, 510, 669, 475, 628, 619, 768, 902, 1025, 493, 695, 602,
    825, 640, 804, 495, 683, 638, 625, 658, 773, 488, 684, 843, 1041, 626, 605, 830, 652, 679, 785,
    722, 761, 798, 864, 653, 678, 771, 728, 755, 784, 591, 612, 799, 698, 681, 820, 727, 576, 807,
    670, 842, 929, 765, 857, 1024, 726, 810, 643, 572, 837, 1029, 733, 610, 691, 828, 718, 897,
    863, 617, 1026, 688, 614, 901, 535, 601, 875, 696, 742, 805, 663, 713, 779, 636, 690, 800, 710,
    1005, 849, 603, 692, 1028, 558, 994, 791, 609, 645, 731, 792, 622, 774, 624, 677, 667, 841,
    639, 806, 724, 824, 650, 909, 753, 823, 766, 786, 873, 644, 651, 797, 752, 767, 818, 835, 876,
    1027, 668, 655, 634, 593, 822, 769, 676, 749, 666, 840, 831, 816, 661, 738, 839, 906, 889,
    1012, 685, 730, 775, 659, 846, 740, 880, 682, 1033, 915, 847, 637, 1008, 1030, 1045, 594, 687,
    700, 793, 772, 871, 706, 673, 632, 735, 790, 815, 912, 993, 716, 586, 629, 699, 918, 809, 709,
    714, 780, 689, 702, 802, 855, 704, 856, 741, 671, 812, 883, 746, 854, 589, 657, 808, 959, 1034,
    850, 708, 693, 715, 781, 887, 888, 896, 1031, 662, 737, 783, 635, 884, 905, 1032, 694, 967,
    1046, 760, 789, 794, 1035, 711, 866, 729, 573, 926, 811, 758, 836, 744, 821, 858, 911, 743,
    977, 1036, 868, 826, 990, 1043, 707, 1037, 853, 701, 686, 801, 890, 1038, 720, 631, 923, 845,
    834, 932, 762, 1040, 725, 1039, 795, 867, 712, 942, 759, 788, 861, 1042, 907, 874, 754, 900,
    869, 1044, 921, 1051, 734, 832, 757, 814, 829, 914, 1047, 987, 717, 872, 933, 950, 1048, 891,
    1057, 844, 751, 903, 944, 827, 1049, 764, 878, 833, 916, 955, 1050, 1059, 970, 885, 848, 763,
    935, 1052, 898, 756, 1058, 859, 745, 1053, 796, 934, 1060, 881, 971, 1067, 1104, 838, 748, 817,
    895, 1054, 1056, 962, 908, 1105, 819, 877, 1055, 899, 924, 1064, 882, 879, 1077, 953, 910,
    1062, 960, 1015, 1065, 1106, 892, 1117, 920, 747, 919, 998, 1061, 1066, 860, 937, 1107, 951,
    966, 1063, 984, 865, 1076, 1096, 862, 943, 1083, 928, 979, 1088, 988, 1103, 1073, 870, 917,
    939, 986, 1068, 1074, 996, 1089, 925, 894, 931, 1070, 978, 1072, 940, 852, 1003, 1069, 1090,
    1075, 904, 886, 945, 975, 1084, 1071, 922, 1152, 948, 965, 1109, 1078, 938, 1098, 982, 949,
    969, 1079, 1080, 1091, 954, 980, 1100, 1189, 1087, 1226, 930, 941, 976, 1093, 1081, 991, 1110,
    936, 913, 1099, 1184, 893, 999, 1082, 968, 1092, 1111, 946, 957, 1193, 961, 974, 1086, 1094,
    947, 956, 1097, 964, 1085, 1122, 1011, 1156, 1163, 973, 1133, 1114, 952, 995, 1153, 972, 1125,
    1115, 1198, 1001, 1168, 927, 1140, 1101, 1155, 992, 1190, 983, 1118, 1136, 1160, 1000, 1159,
    1013, 1123, 1132, 1172, 1002, 1162, 989, 1138, 1121, 1157, 958, 963, 1179, 1102, 1268, 1257,
    1018, 981, 1281, 1119, 1126, 1154, 1004, 1009, 1165, 1112, 1139, 1186, 1006, 985, 1161, 1116,
    1143, 1158, 1010, 1007, 1217, 1130, 1208, 1207, 1284, 1016, 997, 1108, 1131, 1171, 1196, 1014,
    1017, 1141, 1146, 1187, 1180, 1286, 1019, 1237, 1258, 1289, 1170, 1197, 1020, 1328, 1151, 1294,
    1120, 1173, 1021, 1411, 1135, 1210, 1137, 1188, 1023, 1287, 1227, 1150, 1209, 1220, 1314, 1325,
    1360, 1022, 1147, 1166, 1223, 1313, 1200, 1365, 1370, 1215, 1218, 1229, 1312, 1540, 1361, 1203,
    1174, 1225, 1134, 1295, 1280, 1393, 1202, 1177, 1127, 1228, 1290, 1341, 1526, 1233, 1199, 1124,
    1176, 1283, 1292, 1239, 1394, 1129, 1182, 1333, 1416, 1254, 1531, 1537, 1554, 1316, 1181, 1128,
    1206, 1095, 1291, 1397, 1240, 1402, 1169, 1255, 1282, 1293, 1192, 1246, 1395, 1145, 1175, 1285,
    1356, 1262, 1330, 1232, 1183, 1185, 1308, 1347, 1382, 1256, 1247, 1541, 1304, 1303, 1444, 1195,
    1242, 1142, 1221, 1288, 1318, 1401, 1178, 1201, 1231, 1252, 1300, 1315, 1371, 1448, 1271, 1148,
    1167, 1216, 1298, 1309, 1379, 1273, 1214, 1222, 1322, 1301, 1362, 1265, 1389, 1224, 1542, 1307,
    1557, 1251, 1212, 1344, 1390, 1329, 1311, 1219, 1164, 1274, 1380, 1296, 1343, 1538, 1549, 1191,
    1144, 1235, 1310, 1345, 1453, 1442, 1244, 1266, 1358, 1297, 1381, 1338, 1241, 1238, 1414, 1355,
    1457, 1340, 1249, 1539, 1278, 1346, 1357, 1334, 1243, 1337, 1194, 1236, 1349, 1536, 1551, 1331,
    1275, 1452, 1302, 1352, 1546, 1383, 1264, 1279, 1299, 1336, 1412, 1455, 1547, 1269, 1353, 1378,
    1502, 1335, 1548, 1553, 1419, 1204, 1350, 1277, 1339, 1543, 1320, 1234, 1415, 1245, 1377, 1550,
    1332, 1267, 1354, 1276, 1319, 1369, 1428, 1263, 1555, 1248, 1466, 1113, 1348, 1205, 1391, 1544,
    1426, 1559, 1404, 1409, 1230, 1259, 1363, 1552, 1342, 1413, 1614, 1261, 1250, 1392, 1407, 1425,
    1420, 1558, 1576, 1376, 1149, 1427, 1359, 1270, 1556, 1504, 1545, 1306, 1373, 1447, 1575, 1272,
    1618, 1386, 1305, 1422, 1253, 1396, 1568, 1211, 1561, 1474, 1327, 1368, 1574, 1213, 1623, 1430,
    1321, 1489, 1518, 1594, 1567, 1367, 1440, 1433, 1511, 1600, 1566, 1405, 1589, 1435, 1260, 1456,
    1563, 1399, 1570, 1417, 1372, 1514, 1581, 1431, 1584, 1509, 1603, 1384, 1582, 1366, 1437, 1443,
    1585, 1613, 1326, 1490, 1364, 1674, 1441, 1565, 1324, 1587, 1374, 1491, 1602, 1645, 1317, 1564,
    1434, 1403, 1429, 1593, 1505, 1572, 1486, 1406, 1424, 1579, 1461, 1590, 1483, 1388, 1410, 1560,
    1607, 1634, 1421, 1375, 1458, 1596, 1667, 1608, 1477, 1777, 1398, 1450, 1599, 1562, 1481, 1604,
    1460, 1891, 1467, 1592, 1473, 1622, 1438, 1508, 1573, 1578, 1385, 1616, 1432, 1351, 1462, 1569,
    1631, 1640, 1451, 1476, 1464, 1665, 1495, 1586, 1597, 1482, 1323, 1605, 1492, 1650, 1683, 1446,
    1387, 1465, 1580, 1571, 1617, 1408, 1479, 1400, 1471, 1612, 1588, 1666, 1475, 1484, 1463, 1577,
    1591, 1606, 1506, 1436, 1445, 1499, 1595, 1601, 1636, 1480, 1687, 1423, 1521, 1626, 1649, 1498,
    1664, 1454, 1493, 1647, 1660, 1520, 1670, 1688, 1439, 1507, 1619, 1828, 1516, 1661, 1678, 1459,
    1624, 1696, 1468, 1478, 1611, 1669, 1787, 1620, 1469, 1418, 1522, 1701, 1609, 1655, 1533, 1691,
    1494, 1684, 1642, 1761, 1449, 1727, 1488, 1794, 1627, 1637, 1513, 1686, 1510, 1704, 1759, 1635,
    1485, 1744, 1796, 1512, 1689, 1583, 1503, 1633, 1734, 1524, 1672, 1598, 1515, 1657, 1671, 1470,
    1472, 1628, 1527, 1643, 1677, 1714, 1497, 1732, 1532, 1808, 1615, 1705, 1523, 1654, 1496, 1680,
    1797, 1695, 1519, 1651, 1802, 1632, 1869, 1668, 1525, 1530, 1663, 1697, 1803, 1644, 1816, 1487,
    1500, 1703, 1722, 1889, 1812, 1871, 1528, 1751, 1673, 1656, 1698, 1813, 1535, 1798, 1729, 1756,
    1899, 1968,
];

/// Marker ordinal -> codeword index placement table (no reuse or cycling).
pub fn marker_placement() -> &'static [u16] {
    &MARKER_CODEWORDS
}

/// Returns the codeword index assigned to a marker ordinal.
pub fn codeword_of_marker(i: u16) -> Option<u16> {
    MARKER_CODEWORDS.get(usize::from(i)).copied()
}

/// Returns the marker ordinal, or None for a codeword outside the placement.
pub fn marker_of_codeword(c: u16) -> Option<u16> {
    MARKER_CODEWORDS
        .iter()
        .position(|v| *v == c)
        .and_then(|i| u16::try_from(i).ok())
}

#[derive(Debug)]
pub enum RigError {
    Invalid(&'static str),
    Io(io::Error),
    Allocation(std::collections::TryReserveError),
}
impl fmt::Display for RigError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Invalid(s) => write!(f, "invalid chirp configuration: {s}"),
            Self::Io(e) => e.fmt(f),
            Self::Allocation(e) => e.fmt(f),
        }
    }
}
impl std::error::Error for RigError {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        match self {
            Self::Io(e) => Some(e),
            Self::Allocation(e) => Some(e),
            _ => None,
        }
    }
}
impl From<io::Error> for RigError {
    fn from(e: io::Error) -> Self {
        Self::Io(e)
    }
}

/// All lengths are mono sample frames, including for stereo output.
#[derive(Clone, Debug)]
pub struct ChirpConfig {
    pub f_low_hz: f64,
    pub f_high_hz: f64,
    pub sync_len: usize,
    pub bit_len: usize,
    pub gap_len: usize,
    pub taper_fraction: f64,
    pub peak_dbfs: f64,
    pub interval_len: usize,
    pub marker_count: usize,
    pub lead_silence: usize,
    pub tail_silence: usize,
    pub channels: u16,
}
impl Default for ChirpConfig {
    fn default() -> Self {
        Self {
            f_low_hz: 500.0,
            f_high_hz: 4000.0,
            sync_len: 960,
            bit_len: 384,
            gap_len: 96,
            taper_fraction: 0.1,
            peak_dbfs: -12.0,
            interval_len: 24000,
            marker_count: 600,
            lead_silence: 48000,
            tail_silence: 48000,
            channels: 1,
        }
    }
}
const OVERFLOW: RigError = RigError::Invalid("length or RIFF size overflow");
impl ChirpConfig {
    /// Saturates on invalid overflowing configurations; validate before use.
    pub fn marker_len(&self) -> usize {
        self.sync_len
            .saturating_add(self.gap_len)
            .saturating_add(16usize.saturating_mul(self.bit_len.saturating_add(self.gap_len)))
    }
    fn checked_marker_len(&self) -> Result<usize, RigError> {
        self.bit_len
            .checked_add(self.gap_len)
            .and_then(|n| n.checked_mul(16))
            .and_then(|n| n.checked_add(self.sync_len))
            .and_then(|n| n.checked_add(self.gap_len))
            .ok_or(OVERFLOW)
    }
    pub fn total_len(&self) -> Result<usize, RigError> {
        self.marker_count
            .checked_sub(1)
            .and_then(|n| n.checked_mul(self.interval_len))
            .and_then(|n| n.checked_add(self.lead_silence))
            .and_then(|n| n.checked_add(self.tail_silence))
            .and_then(|n| {
                self.checked_marker_len()
                    .ok()
                    .and_then(|m| n.checked_add(m))
            })
            .ok_or(OVERFLOW)
    }
    fn data_bytes(&self) -> Result<u32, RigError> {
        let n = self
            .total_len()?
            .checked_mul(usize::from(self.channels))
            .and_then(|n| n.checked_mul(2))
            .ok_or(OVERFLOW)?;
        let n = u32::try_from(n).map_err(|_| OVERFLOW)?;
        n.checked_add(36).ok_or(OVERFLOW)?;
        Ok(n)
    }
    pub fn validate(&self) -> Result<(), RigError> {
        if !(self.f_low_hz.is_finite()
            && self.f_high_hz.is_finite()
            && 0.0 < self.f_low_hz
            && self.f_low_hz < self.f_high_hz
            && self.f_high_hz <= 0.45 * f64::from(SAMPLE_RATE_HZ))
        {
            return Err(RigError::Invalid("frequency range"));
        }
        if [
            self.sync_len,
            self.bit_len,
            self.gap_len,
            self.interval_len,
            self.lead_silence,
            self.tail_silence,
        ]
        .contains(&0)
        {
            return Err(RigError::Invalid("zero length"));
        }
        if !self.taper_fraction.is_finite() || !(0.0..=0.5).contains(&self.taper_fraction) {
            return Err(RigError::Invalid("taper"));
        }
        if !self.peak_dbfs.is_finite() || !(-60.0..=-1.0).contains(&self.peak_dbfs) {
            return Err(RigError::Invalid("peak"));
        }
        if !(1..=MAX_MARKERS).contains(&self.marker_count) || ![1, 2].contains(&self.channels) {
            return Err(RigError::Invalid("count or channels"));
        }
        if self.interval_len
            < self
                .checked_marker_len()?
                .checked_add(self.gap_len)
                .ok_or(OVERFLOW)?
        {
            return Err(RigError::Invalid("interval"));
        }
        self.data_bytes()?;
        Ok(())
    }
}
pub fn encode_marker_id(index: u16) -> Option<u16> {
    if usize::from(index) >= CODE_SPACE {
        return None;
    }
    // Hamming positions 1..=15 occupy word bits 0..=14. Positions
    // 1, 2, 4, 8 hold even parity; the remaining positions in ascending
    // order hold index bits 0..=10. Bit 15 is overall even parity.
    // marker_template transmits word bits MSB first, including bit 15.
    let mut word = 0u16;
    let mut data_bit = 0;
    let mut syndrome = 0u8;
    for position in 1u8..=15 {
        if !position.is_power_of_two() {
            if index & (1 << data_bit) != 0 {
                word |= 1 << (position - 1);
                syndrome ^= position;
            }
            data_bit += 1;
        }
    }
    for parity in [1u8, 2, 4, 8] {
        if syndrome & parity != 0 {
            word |= 1 << (parity - 1);
        }
    }
    word |= ((word.count_ones() & 1) as u16) << 15;
    Some(word)
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MarkerIdDecode {
    Exact(u16),
    Corrected { index: u16, flipped_bit: u8 },
    Uncorrectable,
}

/// SECDED classification; three or more flipped bits can be misclassified.
pub fn decode_marker_id(mut word: u16) -> MarkerIdDecode {
    let mut syndrome = 0u8;
    for position in 1u8..=15 {
        if word & (1 << (position - 1)) != 0 {
            syndrome ^= position;
        }
    }
    let odd_parity = word.count_ones() & 1 != 0;
    if syndrome != 0 && !odd_parity {
        return MarkerIdDecode::Uncorrectable;
    }
    let flipped_bit = if odd_parity {
        // Syndrome is in 0..=15; zero identifies overall parity bit 15.
        let bit = if syndrome == 0 { 15 } else { syndrome - 1 };
        word ^= 1 << bit;
        Some(bit)
    } else {
        None
    };
    let mut index = 0u16;
    let mut data_bit = 0;
    for position in 1u8..=15 {
        if !position.is_power_of_two() {
            index |= ((word >> (position - 1)) & 1) << data_bit;
            data_bit += 1;
        }
    }
    match flipped_bit {
        Some(flipped_bit) => MarkerIdDecode::Corrected { index, flipped_bit },
        None => MarkerIdDecode::Exact(index),
    }
}
fn chirp(cfg: &ChirpConfig, len: usize, down: bool) -> Vec<f64> {
    if cfg.validate().is_err() {
        return Vec::new();
    }
    let (f0, f1) = if down {
        (cfg.f_high_hz, cfg.f_low_hz)
    } else {
        (cfg.f_low_hz, cfg.f_high_hz)
    };
    let fs = f64::from(SAMPLE_RATE_HZ);
    (0..len)
        .map(|n| {
            let t = n as f64 / fs;
            let edge = n.min(len.saturating_sub(1).saturating_sub(n)) as f64;
            let taper = cfg.taper_fraction * len.saturating_sub(1) as f64;
            let window = if taper > 0.0 && edge < taper {
                0.5 * (1.0 - (std::f64::consts::PI * edge / taper).cos())
            } else {
                1.0
            };
            (std::f64::consts::TAU * (f0 * t + (f1 - f0) * t * t / (2.0 * len as f64 / fs))).sin()
                * window
        })
        .collect()
}
/// Unit-amplitude windowed template. Invalid configurations return an empty vector.
pub fn sync_template(cfg: &ChirpConfig) -> Vec<f64> {
    chirp(cfg, cfg.sync_len, false)
}
/// false = up, true = down. Invalid configurations return an empty vector.
pub fn bit_template(cfg: &ChirpConfig, bit: bool) -> Vec<f64> {
    chirp(cfg, cfg.bit_len, bit)
}
fn zeros<T: Default + Clone>(len: usize) -> Result<Vec<T>, RigError> {
    let mut v = Vec::new();
    v.try_reserve_exact(len).map_err(RigError::Allocation)?;
    v.resize(len, T::default());
    Ok(v)
}
pub fn marker_template(cfg: &ChirpConfig, index: u16) -> Result<Vec<f64>, RigError> {
    cfg.validate()?;
    let word = codeword_of_marker(index)
        .and_then(encode_marker_id)
        .ok_or(RigError::Invalid("marker ID"))?;
    let mut out = zeros(cfg.marker_len())?;
    for (dst, src) in out.iter_mut().zip(sync_template(cfg)) {
        *dst = src;
    }
    let mut start = cfg.sync_len.checked_add(cfg.gap_len).ok_or(OVERFLOW)?;
    for shift in (0..16).rev() {
        let end = start.checked_add(cfg.bit_len).ok_or(OVERFLOW)?;
        let dst = out.get_mut(start..end).ok_or(OVERFLOW)?;
        for (d, s) in dst
            .iter_mut()
            .zip(bit_template(cfg, word & (1 << shift) != 0))
        {
            *d = s;
        }
        start = end.checked_add(cfg.gap_len).ok_or(OVERFLOW)?;
    }
    Ok(out)
}
/// (ID, mono start frame, code word). Validates before computing positions.
pub fn marker_schedule(cfg: &ChirpConfig) -> Result<Vec<(u16, usize, u16)>, RigError> {
    cfg.validate()?;
    (0..cfg.marker_count)
        .map(|k| {
            let id = u16::try_from(k).map_err(|_| OVERFLOW)?;
            let start = k
                .checked_mul(cfg.interval_len)
                .and_then(|n| n.checked_add(cfg.lead_silence))
                .ok_or(OVERFLOW)?;
            Ok((
                id,
                start,
                codeword_of_marker(id)
                    .and_then(encode_marker_id)
                    .ok_or(OVERFLOW)?,
            ))
        })
        .collect()
}
pub fn generate_samples(cfg: &ChirpConfig) -> Result<Vec<i16>, RigError> {
    cfg.validate()?;
    let mut out = zeros(cfg.total_len()?)?;
    let amplitude = 10f64.powf(cfg.peak_dbfs / 20.0) * 32767.0;
    for (id, start, _) in marker_schedule(cfg)? {
        let end = start.checked_add(cfg.marker_len()).ok_or(OVERFLOW)?;
        for (dst, src) in out
            .get_mut(start..end)
            .ok_or(OVERFLOW)?
            .iter_mut()
            .zip(marker_template(cfg, id)?)
        {
            *dst = (src * amplitude)
                .round()
                .clamp(f64::from(i16::MIN), f64::from(i16::MAX)) as i16;
        }
    }
    Ok(out)
}
pub fn write_wav<W: Write>(cfg: &ChirpConfig, mut w: W) -> Result<(), RigError> {
    cfg.validate()?;
    let size = cfg.data_bytes()?;
    let samples = generate_samples(cfg)?;
    w.write_all(b"RIFF")?;
    w.write_all(&size.checked_add(36).ok_or(OVERFLOW)?.to_le_bytes())?;
    w.write_all(b"WAVEfmt ")?;
    w.write_all(&16u32.to_le_bytes())?;
    w.write_all(&1u16.to_le_bytes())?;
    w.write_all(&cfg.channels.to_le_bytes())?;
    w.write_all(&SAMPLE_RATE_HZ.to_le_bytes())?;
    let align = cfg.channels.checked_mul(2).ok_or(OVERFLOW)?;
    w.write_all(
        &SAMPLE_RATE_HZ
            .checked_mul(u32::from(align))
            .ok_or(OVERFLOW)?
            .to_le_bytes(),
    )?;
    w.write_all(&align.to_le_bytes())?;
    w.write_all(&16u16.to_le_bytes())?;
    w.write_all(b"data")?;
    w.write_all(&size.to_le_bytes())?;
    for s in samples {
        for _ in 0..cfg.channels {
            w.write_all(&s.to_le_bytes())?;
        }
    }
    Ok(())
}

/// Cosine quadrature of the unit-amplitude sync template, with the same window.
pub fn sync_template_quadrature(cfg: &ChirpConfig) -> Vec<f64> {
    if cfg.validate().is_err() {
        return Vec::new();
    }
    let len = cfg.sync_len;
    let (f0, f1) = (cfg.f_low_hz, cfg.f_high_hz);
    let fs = f64::from(SAMPLE_RATE_HZ);
    (0..len)
        .map(|n| {
            let t = n as f64 / fs;
            let edge = n.min(len.saturating_sub(1).saturating_sub(n)) as f64;
            let taper = cfg.taper_fraction * len.saturating_sub(1) as f64;
            let window = if taper > 0.0 && edge < taper {
                0.5 * (1.0 - (std::f64::consts::PI * edge / taper).cos())
            } else {
                1.0
            };
            (std::f64::consts::TAU * (f0 * t + (f1 - f0) * t * t / (2.0 * len as f64 / fs))).cos()
                * window
        })
        .collect()
}

/// Cosine quadrature of the bit template (false = up, true = down).
pub fn bit_template_quadrature(cfg: &ChirpConfig, bit: bool) -> Vec<f64> {
    if cfg.validate().is_err() {
        return Vec::new();
    }
    let len = cfg.bit_len;
    let (f0, f1) = if bit {
        (cfg.f_high_hz, cfg.f_low_hz)
    } else {
        (cfg.f_low_hz, cfg.f_high_hz)
    };
    let fs = f64::from(SAMPLE_RATE_HZ);
    (0..len)
        .map(|n| {
            let t = n as f64 / fs;
            let edge = n.min(len.saturating_sub(1).saturating_sub(n)) as f64;
            let taper = cfg.taper_fraction * len.saturating_sub(1) as f64;
            let window = if taper > 0.0 && edge < taper {
                0.5 * (1.0 - (std::f64::consts::PI * edge / taper).cos())
            } else {
                1.0
            };
            (std::f64::consts::TAU * (f0 * t + (f1 - f0) * t * t / (2.0 * len as f64 / fs))).cos()
                * window
        })
        .collect()
}
